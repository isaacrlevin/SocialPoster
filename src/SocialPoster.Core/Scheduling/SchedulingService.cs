using SocialPoster.Core.Services;

namespace SocialPoster.Core.Scheduling;

/// <summary>Validates and snapshots approved content, mentions, account references and durable attachments.</summary>
public sealed class SchedulingService(IScheduledPostStore store, IScheduledMediaStore mediaStore,
    IScheduledCredentialProvider credentials, ContactService contacts, TimeProvider clock,
    ILogger<SchedulingService> logger)
{
    public async Task<ScheduledPost> CreateAsync(ScheduleRequest request, CancellationToken ct = default)
    {
        var post = await PrepareAsync(request, ct);
        var media = await mediaStore.SaveAsync(request.Media, ct);
        if (post.DueAtUtc <= clock.GetUtcNow())
        {
            await mediaStore.DeleteAsync(media, CancellationToken.None);
            throw new ArgumentException("Schedule time elapsed while saving attachments. Choose a future time.");
        }
        post = post with { Media = media };
        // On an ambiguous database error, keep media: the insert might have committed.
        // Orphans are preferable to deleting attachments from a durable, committed schedule.
        await store.InsertAsync(post, ct);
        return post;
    }

    /// <summary>Explicitly approves replacement content/account/mentions. Empty media retains existing attachments.</summary>
    public async Task<ScheduledPost> UpdateAsync(string id, long revision, ScheduleRequest request, CancellationToken ct = default)
    {
        var old = await EditableAsync(id, revision, ct);
        var retained = request.Media.Count == 0 ? await mediaStore.ResolveAsync(old.Media, ct) : request.Media;
        var prepared = await PrepareAsync(request with { Media = retained }, ct);
        var copied = request.Media.Count == 0 ? null : await mediaStore.SaveAsync(request.Media, ct);
        var replacement = prepared with { Id = id, Revision = revision + 1, Media = copied ?? old.Media };
        if (replacement.DueAtUtc <= clock.GetUtcNow())
        {
            if (copied is not null) await mediaStore.DeleteAsync(copied, CancellationToken.None);
            throw new ArgumentException("Schedule time elapsed while saving attachments. Choose a future time.");
        }
        if (!await store.ReplaceAsync(replacement, revision, null, clock.GetUtcNow(), ct))
        {
            if (copied is not null) await mediaStore.DeleteAsync(copied, CancellationToken.None);
            throw new InvalidOperationException("Schedule changed or execution started. Refresh before editing.");
        }
        if (copied is not null)
        {
            try
            {
                await mediaStore.DeleteAsync(old.Media, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Schedule {ScheduleId} was updated, but replaced attachment cleanup failed.", id);
            }
        }
        return replacement;
    }

    /// <summary>Moves a not-yet-attempted post without re-rendering mentions or refreshing its account references.</summary>
    public async Task<ScheduledPost> RescheduleAsync(string id, long revision, DateTime local, string zone,
        TimeSpan? offset = null, CancellationToken ct = default)
    {
        var old = await EditableAsync(id, revision, ct);
        var due = ScheduleTime.Resolve(local, zone, offset, clock.GetUtcNow());
        var post = old with { Revision = revision + 1, DueAtUtc = due, NextAttemptUtc = due,
            LocalDateTime = DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneId = zone, AmbiguousOffset = offset };
        if (!await store.ReplaceAsync(post, revision, null, clock.GetUtcNow(), ct))
            throw new InvalidOperationException("Schedule changed or execution started. Refresh before rescheduling.");
        return post;
    }

    /// <summary>Stops remaining queued deliveries. Active attempts cannot be cancelled safely and are rejected.</summary>
    public async Task CancelAsync(string id, long revision, CancellationToken ct = default)
    {
        var old = await store.GetAsync(id, ct) ?? throw new InvalidOperationException("Schedule not found.");
        if (!await store.ReplaceAsync(old with { State = ScheduleState.Cancelled }, revision, null, clock.GetUtcNow(), ct))
            throw new InvalidOperationException("Schedule changed, completed, or is executing. Refresh before cancelling.");
        // Retain attachments for audit/reconciliation; never delete files a worker might still hold.
    }

    private async Task<ScheduledPost> EditableAsync(string id, long revision, CancellationToken ct)
    {
        var post = await store.GetAsync(id, ct) ?? throw new InvalidOperationException("Schedule not found.");
        if (post.Revision != revision || post.State != ScheduleState.Queued || post.Outcomes.Any(o => o.Attempts != 0))
            throw new InvalidOperationException("Only an unchanged schedule with no delivery attempts can be edited or rescheduled.");
        return post;
    }

    private async Task<ScheduledPost> PrepareAsync(ScheduleRequest request, CancellationToken ct)
    {
        var due = ScheduleTime.Resolve(request.LocalDateTime, request.TimeZoneId, request.AmbiguousOffset, clock.GetUtcNow());
        if (request.Templates.Count == 0) throw new ArgumentException("Select at least one platform.");
        var keys = request.Templates.Values.SelectMany(MentionTemplate.GetMentionKeys).Distinct().ToArray();
        var lookup = await contacts.GetByKeysAsync(keys, ct);
        if (keys.Any(k => !lookup.ContainsKey(k))) throw new ArgumentException("One or more mentions no longer resolve. Review the post before scheduling.");
        var platforms = new List<ScheduledPlatform>();
        foreach (var (platform, template) in request.Templates)
        {
            if (!Enum.IsDefined(platform)) throw new ArgumentException("Unknown platform.");
            var rendered = PostRenderer.Render(platform, template, k => lookup.GetValueOrDefault(k));
            if (rendered.IsEmpty || rendered.IsOverLimit) throw new ArgumentException($"{platform} post is empty or over its character limit.");
            var errors = MediaRules.Validate(platform, request.Media);
            if (request.Media.Any(m => m.AltText?.Length > MediaRules.MaxAltTextLength))
                throw new ArgumentException("Image alt text exceeds 1000 characters.");
            if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors));
            var account = await credentials.ReadAsync(platform, ct);
            // Also validate X intent for replaceable credential providers, not only the local provider.
            if (platform == Platform.X && account.Settings.GetValueOrDefault(SettingKeys.XPostMethod) == "intent")
                throw new InvalidOperationException("X browser-intent posts cannot be scheduled.");
            platforms.Add(new(platform, template, rendered, account.Reference));
        }
        return new()
        {
            DueAtUtc = due, NextAttemptUtc = due, TimeZoneId = request.TimeZoneId,
            LocalDateTime = DateTime.SpecifyKind(request.LocalDateTime, DateTimeKind.Unspecified),
            AmbiguousOffset = request.AmbiguousOffset, BaseTemplate = request.BaseTemplate,
            Platforms = platforms, Outcomes = platforms.Select(p => new DeliveryOutcome(p.Platform)).ToArray(),
        };
    }
}
