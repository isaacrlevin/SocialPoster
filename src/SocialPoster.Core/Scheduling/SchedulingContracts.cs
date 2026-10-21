namespace SocialPoster.Core.Scheduling;

/// <summary>Durable store boundary. Implement all conditional operations atomically on the shared store.</summary>
public interface IScheduledPostStore
{
    Task InsertAsync(ScheduledPost post, CancellationToken ct = default);
    Task<ScheduledPost?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledPost>> ListAsync(CancellationToken ct = default);
    /// <summary>Claims one due or expired-lease aggregate, fencing the previous owner with a new token and revision.</summary>
    Task<ScheduledPost?> ClaimAsync(DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct = default);
    /// <summary>
    /// Compare/exchange. Null owner requires an unleased Queued row; non-null owner requires a matching unexpired lease.
    /// The supplied revision must match; the replacement receives revision + 1. Return false on conflict.
    /// </summary>
    Task<bool> ReplaceAsync(ScheduledPost post, long revision, string? owner, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>Durable attachment boundary. A blob adapter must materialize verified local files for the existing clients.</summary>
public interface IScheduledMediaStore
{
    Task<IReadOnlyList<ScheduledMedia>> SaveAsync(IReadOnlyList<MediaAttachment> media, CancellationToken ct = default);
    Task<IReadOnlyList<MediaAttachment>> ResolveAsync(IReadOnlyList<ScheduledMedia> media, CancellationToken ct = default);
    Task DeleteAsync(IReadOnlyList<ScheduledMedia> media, CancellationToken ct = default);
}

/// <summary>Ephemeral credentials read atomically. Settings must not be serialized into schedule records or logs.</summary>
public sealed record ScheduledCredentials(string Reference, IReadOnlyDictionary<string, string?> Settings);
/// <summary>Replace with a versioned secret/account resolver for non-local hosts.</summary>
public interface IScheduledCredentialProvider
{
    Task<ScheduledCredentials> ReadAsync(Platform platform, CancellationToken ct = default);
}
/// <summary>Executes an already-rendered payload using the supplied immutable credential snapshot.</summary>
public interface IScheduledPlatformPublisher
{
    Task<string> PublishAsync(ScheduledPlatform platform, ScheduledCredentials credentials,
        IReadOnlyList<MediaAttachment> media, CancellationToken ct = default);
}
