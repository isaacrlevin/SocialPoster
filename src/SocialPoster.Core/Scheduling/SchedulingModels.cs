namespace SocialPoster.Core.Scheduling;

/// <summary>Aggregate lifecycle. Completed can contain failed or uncertain platform outcomes.</summary>
public enum ScheduleState { Queued, Running, Completed, Cancelled }
/// <summary>Only Pending and Retry are eligible for automatic execution.</summary>
public enum DeliveryState { Pending, InFlight, Succeeded, Failed, Uncertain, Retry, Expired }

/// <summary>A frozen platform payload and opaque account configuration reference; never contains credentials.</summary>
public sealed record ScheduledPlatform(Platform Platform, string Template, RenderedPost Post, string AccountReference);
/// <summary>Durable media reference, resolved by the media adapter at execution time.</summary>
public sealed record ScheduledMedia(string Key, string FileName, MediaKind Kind, string ContentType, long Size, string? AltText, string Sha256);
/// <summary>Persisted outcome. Uncertain means the remote platform may already have accepted the post.</summary>
public sealed record DeliveryOutcome(Platform Platform, DeliveryState State = DeliveryState.Pending, int Attempts = 0,
    string? Url = null, string? Detail = null, DateTimeOffset? RetryAtUtc = null);

/// <summary>Durable schedule snapshot. Revision is an optimistic concurrency token.</summary>
public sealed record ScheduledPost
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public long Revision { get; init; }
    public ScheduleState State { get; init; } = ScheduleState.Queued;
    public DateTimeOffset DueAtUtc { get; init; }
    public DateTimeOffset NextAttemptUtc { get; init; }
    public string TimeZoneId { get; init; } = "";
    public DateTime LocalDateTime { get; init; }
    public TimeSpan? AmbiguousOffset { get; init; }
    public string BaseTemplate { get; init; } = "";
    public IReadOnlyList<ScheduledPlatform> Platforms { get; init; } = [];
    public IReadOnlyList<ScheduledMedia> Media { get; init; } = [];
    public IReadOnlyList<DeliveryOutcome> Outcomes { get; init; } = [];
    public string? LeaseToken { get; init; }
    public DateTimeOffset? LeaseUntilUtc { get; init; }
}

/// <summary>Input used to create or replace a not-yet-attempted schedule.</summary>
public sealed record ScheduleRequest(string BaseTemplate, IReadOnlyDictionary<Platform, string> Templates,
    IReadOnlyList<MediaAttachment> Media, DateTime LocalDateTime, string TimeZoneId, TimeSpan? AmbiguousOffset = null);

/// <summary>Explicit DST conversion: gaps are rejected; folds require a chosen UTC offset.</summary>
public static class ScheduleTime
{
    public static DateTimeOffset Resolve(DateTime local, string timeZoneId, TimeSpan? ambiguousOffset, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            throw new ArgumentException("This local time does not exist because clocks move forward. Choose another time.");
        TimeSpan offset;
        if (zone.IsAmbiguousTime(local))
        {
            var choices = zone.GetAmbiguousTimeOffsets(local);
            if (ambiguousOffset is null || !choices.Contains(ambiguousOffset.Value))
                throw new ArgumentException("This local time occurs twice. Explicitly choose one of its UTC offsets.");
            offset = ambiguousOffset.Value;
        }
        else
        {
            offset = zone.GetUtcOffset(local);
            if (ambiguousOffset is not null && ambiguousOffset != offset)
                throw new ArgumentException("The selected UTC offset does not match this local time.");
        }
        var instant = new DateTimeOffset(local, offset).ToUniversalTime();
        if (instant <= now) throw new ArgumentException("Schedule time must be in the future.");
        return instant;
    }
}

/// <summary>Polling and bounded retry policy. Old pending work expires rather than publishing unexpectedly.</summary>
public sealed class SchedulingOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(35);
    public TimeSpan MaxLateness { get; set; } = TimeSpan.FromHours(24);
    public int MaxAttempts { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(5);
}
