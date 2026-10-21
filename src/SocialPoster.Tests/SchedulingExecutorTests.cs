using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocialPoster.Core;
using SocialPoster.Core.Scheduling;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

[Trait("Category", "Scheduling")]
public class SchedulingExecutorTests
{
    [Fact]
    public async Task Executor_PartialSuccessAnd429_RetriesOnlyRejectedPlatformWithBoundedBackoff()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        host.Publisher.Behavior = (call, _) => call.Platform.Platform == Platform.X
            ? Task.FromException<string>(new HttpRequestException("redacted", null, HttpStatusCode.TooManyRequests))
            : Task.FromResult("https://example.invalid/confirmed");
        var post = await host.Scheduler.CreateAsync(host.Request("hello", Platform.Bluesky, Platform.X));
        host.MakeDue(post);
        Assert.Equal(1, await host.Executor.RunDueAsync());
        var first = await host.ReadAsync(post.Id);
        Assert.Equal(ScheduleState.Queued, first.State);
        var successful = first.Outcomes.Single(o => o.Platform == Platform.Bluesky);
        Assert.Equal(DeliveryState.Succeeded, successful.State);
        Assert.Equal("https://example.invalid/confirmed", successful.Url);
        Assert.Equal(1, successful.Attempts);
        Assert.Equal(DeliveryState.Retry, first.Outcomes.Single(o => o.Platform == Platform.X).State);
        Assert.Equal(host.Clock.GetUtcNow().AddMinutes(5), first.NextAttemptUtc);
        Assert.Null(first.LeaseToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Scheduler.UpdateAsync(first.Id, first.Revision, host.Request()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Scheduler.RescheduleAsync(first.Id, first.Revision, host.Request().LocalDateTime, "UTC"));
        Assert.Equal(0, await host.Executor.RunDueAsync());
        await host.RestartAsync();
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, await host.Executor.RunDueAsync());
        var second = await host.ReadAsync(post.Id);
        Assert.Equal(host.Clock.GetUtcNow().AddMinutes(10), second.NextAttemptUtc);
        Assert.Equal(2, second.Outcomes.Single(o => o.Platform == Platform.X).Attempts);
        host.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, await host.Executor.RunDueAsync());
        var final = await host.ReadAsync(post.Id);
        Assert.Equal(ScheduleState.Completed, final.State);
        var failed = final.Outcomes.Single(o => o.Platform == Platform.X);
        Assert.Equal(DeliveryState.Failed, failed.State);
        Assert.Equal(3, failed.Attempts);
        Assert.Null(failed.RetryAtUtc);
        Assert.Single(host.Publisher.Calls, c => c.Platform.Platform == Platform.Bluesky);
        Assert.Equal(3, host.Publisher.Calls.Count(c => c.Platform.Platform == Platform.X));
        host.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await host.Executor.RunDueAsync());
        Assert.Equal(4, host.Publisher.Calls.Count);
    }

    [Fact]
    public async Task Executor_429ThenSuccess_CompletesAndNeverRepeats()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        host.Publisher.Behavior = (_, _) => host.Publisher.Calls.Count == 1
            ? Task.FromException<string>(new HttpRequestException("rejected", null, HttpStatusCode.TooManyRequests))
            : Task.FromResult("https://example.invalid/accepted");
        var post = await host.Scheduler.CreateAsync(host.Request());
        host.MakeDue(post);
        await host.Executor.RunDueAsync();
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await host.Executor.RunDueAsync();
        var final = await host.ReadAsync(post.Id);
        Assert.Equal(ScheduleState.Completed, final.State);
        var outcome = Assert.Single(final.Outcomes);
        Assert.Equal(DeliveryState.Succeeded, outcome.State);
        Assert.Equal(2, outcome.Attempts);
        Assert.Equal("https://example.invalid/accepted", outcome.Url);
        Assert.Null(outcome.RetryAtUtc);
        Assert.Equal(0, await host.Executor.RunDueAsync());
        Assert.Equal(2, host.Publisher.Calls.Count);
    }

    [Fact]
    public async Task Executor_TimerTimeout_PersistsInFlightBeforeNetworkThenUncertainWithoutRetry()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Publisher.Behavior = async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "unreachable";
        };
        var post = await host.Scheduler.CreateAsync(host.Request());
        host.MakeDue(post);
        var execution = host.Executor.RunDueAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var inFlight = await host.ReadAsync(post.Id);
        Assert.Equal(ScheduleState.Running, inFlight.State);
        Assert.Equal(DeliveryState.InFlight, Assert.Single(inFlight.Outcomes).State);
        Assert.Equal(1, Assert.Single(inFlight.Outcomes).Attempts);
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await execution.WaitAsync(TimeSpan.FromSeconds(10)));
        var final = await host.ReadAsync(post.Id);
        Assert.Equal(ScheduleState.Completed, final.State);
        Assert.Equal(DeliveryState.Uncertain, Assert.Single(final.Outcomes).State);
        Assert.Null(Assert.Single(final.Outcomes).RetryAtUtc);
        await host.RestartAsync();
        host.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await host.Executor.RunDueAsync());
        Assert.Single(host.Publisher.Calls);
    }

    [Theory]
    [InlineData(400, DeliveryState.Failed)]
    [InlineData(401, DeliveryState.Failed)]
    [InlineData(408, DeliveryState.Uncertain)]
    [InlineData(500, DeliveryState.Uncertain)]
    [InlineData(0, DeliveryState.Uncertain)]
    public async Task Executor_RejectionOrTransportFailure_ClassifiesWithoutRetryOrLeakingException(int status, DeliveryState expected)
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        const string sensitiveBody = "synthetic-sensitive-response-do-not-persist";
        host.Publisher.Behavior = (_, _) => Task.FromException<string>(new HttpRequestException(sensitiveBody,
            null, status == 0 ? null : (HttpStatusCode)status));
        var post = await host.Scheduler.CreateAsync(host.Request());
        host.MakeDue(post);
        await host.Executor.RunDueAsync();
        var final = await host.ReadAsync(post.Id);
        Assert.Equal(ScheduleState.Completed, final.State);
        var outcome = Assert.Single(final.Outcomes);
        Assert.Equal(expected, outcome.State);
        Assert.Equal(1, outcome.Attempts);
        Assert.DoesNotContain(sensitiveBody, outcome.Detail!);
        host.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await host.Executor.RunDueAsync());
        Assert.Single(host.Publisher.Calls);
    }

    [Fact]
    public async Task Executor_HostCancellationDuringAttempt_UncertainRecordedAndNeverRepeated()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Publisher.Behavior = async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "unreachable";
        };
        var post = await host.Scheduler.CreateAsync(host.Request());
        host.MakeDue(post);
        var execution = host.Executor.RunDueAsync(maxPosts: 1, ct: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DeliveryState.Uncertain, Assert.Single((await host.ReadAsync(post.Id)).Outcomes).State);
        Assert.Equal(0, await host.Executor.RunDueAsync());
        Assert.Single(host.Publisher.Calls);
    }

    [Theory]
    [InlineData(1440, DeliveryState.Succeeded, 1)]
    [InlineData(1441, DeliveryState.Expired, 0)]
    public async Task Executor_OverdueWindowBoundary_ExpiresOnlyBeyondLimit(int lateMinutes, DeliveryState state, int calls)
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var post = await host.Scheduler.CreateAsync(host.Request());
        host.MakeDue(post);
        host.Clock.Advance(TimeSpan.FromMinutes(lateMinutes));
        await host.Executor.RunDueAsync();
        var final = await host.ReadAsync(post.Id);
        Assert.Equal(ScheduleState.Completed, final.State);
        var outcome = Assert.Single(final.Outcomes);
        Assert.Equal(state, outcome.State);
        Assert.Equal(calls, outcome.Attempts);
        Assert.Equal(calls, host.Publisher.Calls.Count);
        Assert.Equal(0, await host.Executor.RunDueAsync());
    }

    [Fact]
    public async Task Executor_ChangedAccount_PreflightFailsWithZeroAttempts()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var post = await host.Scheduler.CreateAsync(host.Request());
        host.Credentials.Reference = "different-account";
        host.MakeDue(post);
        await host.Executor.RunDueAsync();
        var final = await host.ReadAsync(post.Id);
        Assert.Equal(ScheduleState.Completed, final.State);
        Assert.Equal(DeliveryState.Failed, Assert.Single(final.Outcomes).State);
        Assert.Equal(0, Assert.Single(final.Outcomes).Attempts);
        Assert.Empty(host.Publisher.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executor_MissingOrTamperedMedia_DoesNotPublish(bool tampered)
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var post = await host.Scheduler.CreateAsync(host.Request() with { Media = [await host.UploadAsync()] });
        var copy = Assert.Single(await host.Media.ResolveAsync(post.Media));
        if (tampered) await File.WriteAllBytesAsync(copy.Path!, [4, 3, 2, 1]);
        else File.Delete(copy.Path!);
        host.MakeDue(post);
        await host.Executor.RunDueAsync();
        var outcome = Assert.Single((await host.ReadAsync(post.Id)).Outcomes);
        Assert.Equal(DeliveryState.Failed, outcome.State);
        Assert.Equal(0, outcome.Attempts);
        Assert.DoesNotContain(host.DirectoryPath, outcome.Detail!);
        Assert.Empty(host.Publisher.Calls);
    }

    [Fact]
    public async Task Executor_SourceDeleted_PublishesDurableMediaAndAltText()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var upload = await host.UploadAsync("Frozen description");
        var post = await host.Scheduler.CreateAsync(host.Request() with { Media = [upload] });
        upload.AltText = "Changed composer description";
        File.Delete(upload.Path!);
        await host.RestartAsync();
        host.MakeDue(post);
        await host.Executor.RunDueAsync();
        var media = Assert.Single(Assert.Single(host.Publisher.Calls).Media);
        Assert.Equal("Frozen description", media.AltText);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(media.Path!));
    }

    [Fact]
    public async Task Executor_ConcurrentRuns_NoDuplicatePublicationAndMaxPostsHonored()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var posts = new List<ScheduledPost>();
        for (var i = 0; i < 3; i++) posts.Add(await host.Scheduler.CreateAsync(host.Request($"post-{i}")));
        host.MakeDue(posts[0]);
        Assert.Equal(1, await host.Executor.RunDueAsync(maxPosts: 1));
        Assert.Single(host.Publisher.Calls);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            await using var scope = host.Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ScheduledPostExecutor>().RunDueAsync();
        })).ToArray();
        start.SetResult();
        Assert.Equal(2, (await Task.WhenAll(runs)).Sum());
        Assert.Equal(3, host.Publisher.Calls.Count);
        Assert.Equal(3, host.Publisher.Calls.Select(c => c.Platform.Post.DisplayText).Distinct().Count());
        Assert.All(await host.Store.ListAsync(), p => Assert.Equal(ScheduleState.Completed, p.State));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.Executor.RunDueAsync(0));
    }

    [Fact]
    public async Task Executor_CancelQueuedRetry_NoFurtherNetworkCalls()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        host.Publisher.Behavior = (_, _) => Task.FromException<string>(new HttpRequestException("rejected", null, HttpStatusCode.TooManyRequests));
        var post = await host.Scheduler.CreateAsync(host.Request());
        host.MakeDue(post);
        await host.Executor.RunDueAsync();
        var retry = await host.ReadAsync(post.Id);
        await host.Scheduler.CancelAsync(retry.Id, retry.Revision);
        host.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await host.Executor.RunDueAsync());
        Assert.Single(host.Publisher.Calls);
        Assert.Equal(ScheduleState.Cancelled, (await host.ReadAsync(post.Id)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task X_BrowserIntentExcludedAtApprovalAndExecution(bool afterApproval)
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        if (!afterApproval)
        {
            host.Credentials.Settings[SettingKeys.XPostMethod] = "intent";
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.Scheduler.CreateAsync(host.Request("hello", Platform.X)));
            Assert.Empty(await host.Store.ListAsync());
        }
        else
        {
            var post = await host.Scheduler.CreateAsync(host.Request("hello", Platform.X));
            // Even a replacement provider with an unchanged fingerprint cannot authorize intent.
            host.Credentials.Settings[SettingKeys.XPostMethod] = "intent";
            host.MakeDue(post);
            await host.Executor.RunDueAsync();
            var outcome = Assert.Single((await host.ReadAsync(post.Id)).Outcomes);
            Assert.Equal(DeliveryState.Failed, outcome.State);
            Assert.Equal(0, outcome.Attempts);
        }
        Assert.Empty(host.Publisher.Calls);
    }

    [Fact]
    public async Task LocalCredentials_CiphertextRotationInvalidatesApprovedScheduleWithoutPersistingSecrets()
    {
        await using var host = await SchedulingTestHost.CreateAsync(localCredentials: true);
        var secrets = host.Services.GetRequiredService<SecretStore>();
        await secrets.SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.BlueskyHandle] = "synthetic-account.test",
            [SettingKeys.BlueskyAppPassword] = "synthetic-not-a-real-password",
        });
        var credentials = host.Services.GetRequiredService<IScheduledCredentialProvider>();
        var before = await credentials.ReadAsync(Platform.Bluesky);
        var post = await host.Scheduler.CreateAsync(host.Request());
        Assert.Equal(before.Reference, Assert.Single(post.Platforms).AccountReference);
        // Unrelated settings do not invalidate this account.
        await secrets.SetAsync(CopilotSettingKeys.Enabled, "false");
        Assert.Equal(before.Reference, (await credentials.ReadAsync(Platform.Bluesky)).Reference);
        await host.RestartAsync();
        Assert.Equal(before.Reference, (await host.Services.GetRequiredService<IScheduledCredentialProvider>().ReadAsync(Platform.Bluesky)).Reference);
        // Saving the same plaintext produces a new ciphertext snapshot and requires re-approval.
        await host.Services.GetRequiredService<SecretStore>().SetAsync(SettingKeys.BlueskyAppPassword, "synthetic-not-a-real-password");
        var after = await host.Services.GetRequiredService<IScheduledCredentialProvider>().ReadAsync(Platform.Bluesky);
        Assert.NotEqual(before.Reference, after.Reference);
        Assert.Equal(before.Settings[SettingKeys.BlueskyAppPassword], after.Settings[SettingKeys.BlueskyAppPassword]);
        host.MakeDue(post);
        await host.Executor.RunDueAsync();
        Assert.Equal(DeliveryState.Failed, Assert.Single((await host.ReadAsync(post.Id)).Outcomes).State);
        Assert.Empty(host.Publisher.Calls);
        await using var db = await host.Factory.CreateDbContextAsync();
        var json = await db.Database.SqlQuery<string>($"SELECT \"Json\" AS \"Value\" FROM \"ScheduledPosts\" WHERE \"Id\" = {post.Id}").SingleAsync();
        Assert.DoesNotContain("synthetic-not-a-real-password", json);
        Assert.DoesNotContain("synthetic-account.test", json);
        Assert.All(await db.Settings.ToListAsync(), s => Assert.NotEqual("synthetic-not-a-real-password", s.ProtectedValue));
    }

    [Fact]
    public async Task LocalCredentials_ConfiguredUnchangedAccount_ExecutesWithDecryptedSnapshot()
    {
        await using var host = await SchedulingTestHost.CreateAsync(localCredentials: true);
        await host.Services.GetRequiredService<SecretStore>().SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.BlueskyHandle] = "synthetic-account.test",
            [SettingKeys.BlueskyAppPassword] = "synthetic-password",
        });
        var post = await host.Scheduler.CreateAsync(host.Request());
        host.MakeDue(post);
        await host.Executor.RunDueAsync();
        var call = Assert.Single(host.Publisher.Calls);
        Assert.Equal("synthetic-account.test", call.Credentials.Settings[SettingKeys.BlueskyHandle]);
        Assert.Equal("synthetic-password", call.Credentials.Settings[SettingKeys.BlueskyAppPassword]);
        Assert.Equal(Assert.Single(post.Platforms).AccountReference, call.Credentials.Reference);
        Assert.Equal(DeliveryState.Succeeded, Assert.Single((await host.ReadAsync(post.Id)).Outcomes).State);
    }

    [Fact]
    public async Task LocalCredentials_MissingAndUndecryptableSettings_RejectApproval()
    {
        await using var host = await SchedulingTestHost.CreateAsync(localCredentials: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Scheduler.CreateAsync(host.Request()));
        await host.Services.GetRequiredService<SecretStore>().SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.BlueskyHandle] = "synthetic-account.test",
            [SettingKeys.BlueskyAppPassword] = "synthetic-password",
        });
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var row = await db.Settings.SingleAsync(s => s.Key == SettingKeys.BlueskyAppPassword);
            row.ProtectedValue = new EphemeralDataProtectionProvider().CreateProtector("SocialPoster.Settings.v1").Protect("synthetic-password");
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Scheduler.CreateAsync(host.Request()));
        Assert.Empty(await host.Store.ListAsync());
        Assert.Empty(host.Publisher.Calls);
    }

    [Fact]
    public async Task LocalCredentials_XIntentWithConfiguredApiKeys_StillRejectsApproval()
    {
        await using var host = await SchedulingTestHost.CreateAsync(localCredentials: true);
        await host.Services.GetRequiredService<SecretStore>().SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.XConsumerKey] = "synthetic-key",
            [SettingKeys.XConsumerSecret] = "synthetic-secret",
            [SettingKeys.XAccessToken] = "synthetic-token",
            [SettingKeys.XAccessTokenSecret] = "synthetic-token-secret",
            [SettingKeys.XPostMethod] = "intent",
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Scheduler.CreateAsync(host.Request("hello", Platform.X)));
        Assert.Empty(await host.Store.ListAsync());
        Assert.Empty(host.Publisher.Calls);
    }
}
