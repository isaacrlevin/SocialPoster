using System.Net;
using Microsoft.Extensions.Options;
using SocialPoster.Core.Services;

namespace SocialPoster.Core.Scheduling;

/// <summary>
/// Host-independent polling entry point for a timer Function, a worker, or an explicitly invoked command.
/// Records InFlight before any remote call; expired InFlight is never automatically retried.
/// </summary>
public sealed class ScheduledPostExecutor(IScheduledPostStore store, IScheduledMediaStore mediaStore,
    IScheduledCredentialProvider credentials, IScheduledPlatformPublisher publisher,
    IOptions<SchedulingOptions> options, TimeProvider clock)
{
    /// <summary>Claims and processes at most maxPosts aggregates. Safe for concurrent callers against a shared store.</summary>
    public async Task<int> RunDueAsync(int maxPosts = 10, CancellationToken ct = default)
    {
        if (maxPosts < 1) throw new ArgumentOutOfRangeException(nameof(maxPosts));
        var processed = 0;
        while (processed < maxPosts)
        {
            ct.ThrowIfCancellationRequested();
            var job = await store.ClaimAsync(clock.GetUtcNow(), options.Value.LeaseDuration, ct);
            if (job is null) break;
            await ExecuteAsync(job, ct);
            processed++;
        }
        return processed;
    }

    private async Task ExecuteAsync(ScheduledPost job, CancellationToken ct)
    {
        foreach (var platform in job.Platforms)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = job.Outcomes.Single(o => o.Platform == platform.Platform);
            if (outcome.State is not (DeliveryState.Pending or DeliveryState.Retry)) continue;
            if (outcome.RetryAtUtc > clock.GetUtcNow()) continue;
            if (clock.GetUtcNow() - job.DueAtUtc > options.Value.MaxLateness)
            {
                var expired = outcome with { State = DeliveryState.Expired, Detail = "Overdue beyond the configured lateness window; no delivery attempted." };
                var saved = await SaveOutcomeAsync(job, expired, ct);
                if (saved is null) return;
                job = saved;
                continue;
            }

            ScheduledCredentials account;
            IReadOnlyList<MediaAttachment> attachments;
            try
            {
                account = await credentials.ReadAsync(platform.Platform, ct);
                if (account.Reference != platform.AccountReference)
                    throw new InvalidOperationException("Account configuration changed.");
                if (platform.Platform == Platform.X && account.Settings.GetValueOrDefault(SettingKeys.XPostMethod) == "intent")
                    throw new InvalidOperationException("X browser intent cannot execute unattended.");
                attachments = await mediaStore.ResolveAsync(job.Media, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                // No network call occurred. Do not persist credential values, file paths, or exception bodies.
                var failed = outcome with { State = DeliveryState.Failed, Detail = "Preflight failed: account changed/unavailable, unsupported posting mode, or durable media missing/changed. Review and create a new schedule." };
                var saved = await SaveOutcomeAsync(job, failed, ct);
                if (saved is null) return;
                job = saved;
                continue;
            }

            outcome = outcome with { State = DeliveryState.InFlight, Attempts = outcome.Attempts + 1, RetryAtUtc = null, Detail = null };
            var started = await SaveOutcomeAsync(job, outcome, ct);
            if (started is null) return;
            job = started;

            // The lease covers each attempt, including uploads. Extend it atomically before beginning a new platform.
            var extended = job with { LeaseUntilUtc = clock.GetUtcNow() + options.Value.LeaseDuration };
            if (!await store.ReplaceAsync(extended, job.Revision, job.LeaseToken, clock.GetUtcNow(), ct)) return;
            job = extended with { Revision = job.Revision + 1 };
            using var timeout = new CancellationTokenSource(options.Value.AttemptTimeout, clock);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                var url = await publisher.PublishAsync(platform, account, attachments, attempt.Token);
                outcome = outcome with { State = DeliveryState.Succeeded, Url = url };
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Explicit rejection, not a lost response. This is the only default automatic network retry.
                var retry = outcome.Attempts < options.Value.MaxAttempts;
                outcome = outcome with
                {
                    State = retry ? DeliveryState.Retry : DeliveryState.Failed,
                    RetryAtUtc = retry ? clock.GetUtcNow() + options.Value.RetryDelay * outcome.Attempts : null,
                    Detail = retry ? "Platform rejected the attempt with HTTP 429; bounded retry queued." : "HTTP 429 retry limit reached; no successful delivery confirmed.",
                };
            }
            catch (HttpRequestException ex) when (ex.StatusCode is { } status && (int)status is >= 400 and < 500 && status != HttpStatusCode.RequestTimeout)
            {
                outcome = outcome with { State = DeliveryState.Failed, Detail = $"Platform rejected the request (HTTP {(int)ex.StatusCode!}); automatic retry disabled." };
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException or System.Text.Json.JsonException)
            {
                outcome = outcome with { State = DeliveryState.Uncertain, Detail = "Delivery may have succeeded: timeout, transport, processing, or response failure. Check the platform; automatic retry disabled." };
            }
            // Record even on host shutdown. If the lease was lost, recovery will mark the persisted InFlight uncertain.
            var completed = await SaveOutcomeAsync(job, outcome, CancellationToken.None);
            if (completed is null) return;
            job = completed;
            if (ct.IsCancellationRequested) break;
        }
        var outstanding = job.Outcomes.Where(o => o.State is DeliveryState.Pending or DeliveryState.Retry).ToArray();
        var next = outstanding.Length == 0 ? job.NextAttemptUtc :
            outstanding.Min(o => o.RetryAtUtc ?? clock.GetUtcNow());
        var finished = job with
        {
            State = outstanding.Length == 0 ? ScheduleState.Completed : ScheduleState.Queued,
            NextAttemptUtc = next, LeaseToken = null, LeaseUntilUtc = null,
        };
        await store.ReplaceAsync(finished, job.Revision, job.LeaseToken, clock.GetUtcNow(), CancellationToken.None);
    }

    private async Task<ScheduledPost?> SaveOutcomeAsync(ScheduledPost job, DeliveryOutcome outcome, CancellationToken ct)
    {
        var replacement = job with { Outcomes = job.Outcomes.Select(o => o.Platform == outcome.Platform ? outcome : o).ToArray() };
        return await store.ReplaceAsync(replacement, job.Revision, job.LeaseToken, clock.GetUtcNow(), ct)
            ? replacement with { Revision = job.Revision + 1 } : null;
    }
}
