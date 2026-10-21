using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SocialPoster.Core;
using SocialPoster.Core.Scheduling;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

[Trait("Category", "Scheduling")]
public class SchedulingStoreAndServiceTests
{
    [Fact]
    public async Task Store_ConcurrentClaimAcrossFactories_ExactlyOneOwner()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var post = await host.Scheduler.CreateAsync(host.Request());
        host.MakeDue(post);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            await using var scope = host.Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IScheduledPostStore>()
                .ClaimAsync(host.Clock.GetUtcNow(), TimeSpan.FromMinutes(2));
        })).ToArray();
        start.SetResult();
        var claimed = Assert.Single(await Task.WhenAll(attempts), p => p is not null)!;
        Assert.Equal(post.Id, claimed.Id);
        Assert.Equal(post.Revision + 1, claimed.Revision);
        Assert.Equal(ScheduleState.Running, claimed.State);
        Assert.NotNull(claimed.LeaseToken);
        var persisted = await host.ReadAsync(post.Id);
        Assert.Equal(claimed.LeaseToken, persisted.LeaseToken);
        Assert.Equal(claimed.LeaseUntilUtc, persisted.LeaseUntilUtc);
    }

    [Fact]
    public async Task Store_ConcurrentRevisionCompareExchange_ExactlyOneReplacement()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var post = await host.Scheduler.CreateAsync(host.Request());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacements = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await start.Task;
            var store = new SqliteScheduledPostStore(host.Factory);
            return await store.ReplaceAsync(post with { BaseTemplate = $"edit-{i}" },
                post.Revision, null, host.Clock.GetUtcNow());
        })).ToArray();
        start.SetResult();
        Assert.Single(await Task.WhenAll(replacements), success => success);
        Assert.Equal(post.Revision + 1, (await host.ReadAsync(post.Id)).Revision);
    }

    [Fact]
    public async Task Store_LeaseAndRevisionFences_RejectsStaleWrongExpiredAndUnownedWrites()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var post = await host.Scheduler.CreateAsync(host.Request());
        Assert.Null(await host.Store.ClaimAsync(host.Clock.GetUtcNow(), TimeSpan.FromMinutes(2)));
        host.MakeDue(post);
        var lease = (await host.Store.ClaimAsync(host.Clock.GetUtcNow(), TimeSpan.FromMinutes(2)))!;
        Assert.False(await host.Store.ReplaceAsync(lease, post.Revision, lease.LeaseToken, host.Clock.GetUtcNow()));
        Assert.False(await host.Store.ReplaceAsync(lease, lease.Revision, "wrong-owner", host.Clock.GetUtcNow()));
        Assert.False(await host.Store.ReplaceAsync(lease, lease.Revision, null, host.Clock.GetUtcNow()));
        Assert.True(await host.Store.ReplaceAsync(lease, lease.Revision, lease.LeaseToken, host.Clock.GetUtcNow()));
        lease = await host.ReadAsync(post.Id);
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(await host.Store.ReplaceAsync(lease, lease.Revision, lease.LeaseToken, host.Clock.GetUtcNow()));
        var recovered = (await host.Store.ClaimAsync(host.Clock.GetUtcNow(), TimeSpan.FromMinutes(2)))!;
        Assert.NotEqual(lease.LeaseToken, recovered.LeaseToken);
        Assert.Equal(lease.Revision + 1, recovered.Revision);
        Assert.False(await host.Store.ReplaceAsync(recovered, recovered.Revision, lease.LeaseToken, host.Clock.GetUtcNow()));
        Assert.True(await host.Store.ReplaceAsync(recovered, recovered.Revision, recovered.LeaseToken, host.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task Store_ExpiredInFlightAfterRestart_BecomesUncertainNeverPublishedAgain()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var post = await host.Scheduler.CreateAsync(host.Request());
        host.MakeDue(post);
        var leased = (await host.Store.ClaimAsync(host.Clock.GetUtcNow(), TimeSpan.FromMinutes(2)))!;
        Assert.True(await host.Store.ReplaceAsync(leased with
        {
            Outcomes = [new(Platform.Bluesky, DeliveryState.InFlight, Attempts: 1)],
        }, leased.Revision, leased.LeaseToken, host.Clock.GetUtcNow()));
        await host.RestartAsync();
        Assert.Equal(0, await host.Executor.RunDueAsync());
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await host.Executor.RunDueAsync());
        var result = await host.ReadAsync(post.Id);
        Assert.Equal(ScheduleState.Completed, result.State);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(DeliveryState.Uncertain, outcome.State);
        Assert.Equal(1, outcome.Attempts);
        Assert.Contains("lease expired", outcome.Detail);
        Assert.Empty(host.Publisher.Calls);
        host.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(0, await host.Executor.RunDueAsync());
    }

    [Fact]
    public async Task Store_ExpiredLeaseMixedOutcomes_RecoversPendingWithoutRepeatingSuccessfulOrUncertain()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var post = await host.Scheduler.CreateAsync(host.Request("hello", Platform.Bluesky, Platform.X, Platform.LinkedIn));
        host.MakeDue(post);
        var leased = (await host.Store.ClaimAsync(host.Clock.GetUtcNow(), TimeSpan.FromMinutes(2)))!;
        Assert.True(await host.Store.ReplaceAsync(leased with
        {
            Outcomes =
            [
                new(Platform.Bluesky, DeliveryState.Succeeded, 1, "https://example.invalid/already-posted"),
                new(Platform.X, DeliveryState.InFlight, 1),
                new(Platform.LinkedIn),
            ],
        }, leased.Revision, leased.LeaseToken, host.Clock.GetUtcNow()));
        await host.RestartAsync();
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        await host.Executor.RunDueAsync();
        var result = await host.ReadAsync(post.Id);
        Assert.Equal(ScheduleState.Completed, result.State);
        Assert.Equal(DeliveryState.Uncertain, result.Outcomes.Single(o => o.Platform == Platform.X).State);
        Assert.Equal("https://example.invalid/already-posted", result.Outcomes.Single(o => o.Platform == Platform.Bluesky).Url);
        Assert.Equal(Platform.LinkedIn, Assert.Single(host.Publisher.Calls).Platform.Platform);
        Assert.All(result.Outcomes, o => Assert.Equal(1, o.Attempts));
        Assert.Equal(0, await host.Executor.RunDueAsync());
    }

    [Fact]
    public async Task Service_CreateEditRescheduleRestartCancel_PreservesSnapshotAndRevision()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var upload = await host.UploadAsync();
        var post = await host.Scheduler.CreateAsync(host.Request() with { Media = [upload] });
        File.Delete(upload.Path!);
        Assert.Equal(ScheduleState.Queued, post.State);
        Assert.Equal(0, post.Revision);
        Assert.Equal(DeliveryState.Pending, Assert.Single(post.Outcomes).State);
        var edited = await host.Scheduler.UpdateAsync(post.Id, post.Revision, host.Request("Edited text"));
        Assert.Equal(1, edited.Revision);
        Assert.Equal(post.Media, edited.Media);
        Assert.Equal("Edited text", Assert.Single(edited.Platforms).Post.DisplayText);
        host.Credentials.Reference = "changed-after-approval";
        var moved = await host.Scheduler.RescheduleAsync(edited.Id, edited.Revision,
            host.Clock.GetUtcNow().AddHours(3).UtcDateTime, "UTC");
        Assert.Equal(2, moved.Revision);
        Assert.Equal(Assert.Single(edited.Platforms).AccountReference, Assert.Single(moved.Platforms).AccountReference);
        await host.RestartAsync();
        var durable = await host.ReadAsync(post.Id);
        Assert.Equal(moved.DueAtUtc, durable.DueAtUtc);
        Assert.Equal(moved.NextAttemptUtc, durable.NextAttemptUtc);
        Assert.Equal(moved.TimeZoneId, durable.TimeZoneId);
        Assert.Equal(DateTimeKind.Unspecified, durable.LocalDateTime.Kind);
        Assert.Equal("Edited text", Assert.Single(durable.Platforms).Post.PayloadText);
        Assert.Equal(post.Media.Single().Sha256, durable.Media.Single().Sha256);
        Assert.Single(await host.Store.ListAsync());
        await host.Scheduler.CancelAsync(durable.Id, durable.Revision);
        var cancelled = await host.ReadAsync(post.Id);
        Assert.Equal(ScheduleState.Cancelled, cancelled.State);
        Assert.Equal(3, cancelled.Revision);
        Assert.Single(await host.Media.ResolveAsync(cancelled.Media));
        host.MakeDue(cancelled);
        Assert.Equal(0, await host.Executor.RunDueAsync());
        Assert.Empty(host.Publisher.Calls);
    }

    [Fact]
    public async Task Service_UpdateReplacementMedia_DeletesOldCopyOnlyAfterApproval()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var post = await host.Scheduler.CreateAsync(host.Request() with { Media = [await host.UploadAsync("old")] });
        var oldPath = Assert.Single(await host.Media.ResolveAsync(post.Media)).Path!;
        var edited = await host.Scheduler.UpdateAsync(post.Id, post.Revision,
            host.Request("replacement") with { Media = [await host.UploadAsync("new")] });
        Assert.False(File.Exists(oldPath));
        Assert.NotEqual(post.Media.Single().Key, edited.Media.Single().Key);
        Assert.Equal("new", Assert.Single(await host.Media.ResolveAsync(edited.Media)).AltText);
    }

    [Fact]
    public async Task Service_StaleRevisionAndRunningSchedule_RejectsEditsRescheduleAndCancel()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var post = await host.Scheduler.CreateAsync(host.Request());
        var edited = await host.Scheduler.UpdateAsync(post.Id, post.Revision, host.Request("new"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Scheduler.UpdateAsync(post.Id, post.Revision, host.Request()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Scheduler.RescheduleAsync(post.Id, post.Revision, host.Request().LocalDateTime, "UTC"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Scheduler.CancelAsync(post.Id, post.Revision));
        host.MakeDue(edited);
        await host.Store.ClaimAsync(host.Clock.GetUtcNow(), TimeSpan.FromMinutes(2));
        var running = await host.ReadAsync(post.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Scheduler.UpdateAsync(post.Id, running.Revision, host.Request()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Scheduler.RescheduleAsync(post.Id, running.Revision, host.Clock.GetUtcNow().AddHours(1).UtcDateTime, "UTC"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Scheduler.CancelAsync(post.Id, running.Revision));
        Assert.Equal(ScheduleState.Running, (await host.ReadAsync(post.Id)).State);
    }

    [Fact]
    public async Task Service_ContactsEditedAfterApproval_ExecutionUsesFrozenMentionTextAndFacets()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.Add(new Contact { Key = "friend", DisplayName = "Original",
                BlueskyHandle = "original.test", BlueskyDid = "did:plc:original" });
            await db.SaveChangesAsync();
        }
        var post = await host.Scheduler.CreateAsync(host.Request("Hi {@friend}"));
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var friend = await db.Contacts.SingleAsync();
            friend.DisplayName = "Changed";
            friend.BlueskyHandle = "changed.test";
            friend.BlueskyDid = "did:plc:changed";
            await db.SaveChangesAsync();
        }
        var moved = await host.Scheduler.RescheduleAsync(post.Id, post.Revision,
            host.Clock.GetUtcNow().AddMinutes(20).UtcDateTime, "UTC");
        await host.RestartAsync();
        host.MakeDue(moved);
        Assert.Equal(1, await host.Executor.RunDueAsync());
        var sent = Assert.Single(host.Publisher.Calls).Platform.Post;
        Assert.Equal("Hi @original.test", sent.DisplayText);
        Assert.Equal("did:plc:original", Assert.Single(sent.Facets).Value);
    }

    [Fact]
    public async Task Service_UpdateExplicitlyApprovesChangedAccountAndMention()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.Add(new Contact { Key = "friend", DisplayName = "Original", BlueskyHandle = "old.test", BlueskyDid = "did:plc:old" });
            await db.SaveChangesAsync();
        }
        var post = await host.Scheduler.CreateAsync(host.Request("Hi {@friend}"));
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var friend = await db.Contacts.SingleAsync();
            friend.BlueskyHandle = "new.test";
            friend.BlueskyDid = "did:plc:new";
            await db.SaveChangesAsync();
        }
        host.Credentials.Reference = "new-approved-account";
        var edited = await host.Scheduler.UpdateAsync(post.Id, post.Revision, host.Request("Hi {@friend}"));
        host.MakeDue(edited);
        await host.Executor.RunDueAsync();
        var sent = Assert.Single(host.Publisher.Calls);
        Assert.Equal("Hi @new.test", sent.Platform.Post.DisplayText);
        Assert.Equal("did:plc:new", Assert.Single(sent.Platform.Post.Facets).Value);
        Assert.Equal("new-approved-account", sent.Credentials.Reference);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("overlimit")]
    [InlineData("mention")]
    [InlineData("platform")]
    [InlineData("none")]
    [InlineData("past")]
    [InlineData("alt")]
    public async Task Service_InvalidRequest_NoScheduleOrDurableCopy(string invalid)
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var request = host.Request();
        request = invalid switch
        {
            "empty" => host.Request(" "),
            "overlimit" => host.Request(new string('a', 301)),
            "mention" => host.Request("Hello {@missing}"),
            "platform" => host.Request("Hello", (Platform)99),
            "none" => request with { Templates = new Dictionary<Platform, string>() },
            "past" => request with { LocalDateTime = host.Clock.GetUtcNow().UtcDateTime },
            "alt" => request with { Media = [await host.UploadAsync(new string('a', 1001))] },
            _ => throw new InvalidOperationException(),
        };
        await Assert.ThrowsAsync<ArgumentException>(() => host.Scheduler.CreateAsync(request));
        Assert.Empty(await host.Store.ListAsync());
        Assert.False(Directory.Exists(Path.Combine(host.DirectoryPath, "scheduled-media")));
    }

    [Fact]
    public async Task Registration_SchedulingDoesNotStartWorkerOrReplaceHostProtection()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        Assert.DoesNotContain(host.Provider.GetServices<IHostedService>(), s => s is SocialPosterSchedulingWorker);
        Assert.Same(host.Protection, host.Provider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>());
        Assert.IsType<SqliteScheduledPostStore>(host.Store);
        Assert.IsType<FileScheduledMediaStore>(host.Media);
        var services = new ServiceCollection();
        services.AddSocialPosterScheduling();
        services.AddSocialPosterSchedulingWorker();
        services.AddSocialPosterSchedulingWorker();
        var worker = Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(typeof(SocialPosterSchedulingWorker), worker.ImplementationType);
    }

    [Fact]
    public async Task Worker_ExplicitlyStarted_ResolvesScopedExecutorAndProcessesDueWork()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var post = await host.Scheduler.CreateAsync(host.Request());
        host.MakeDue(post);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Publisher.Behavior = (_, _) =>
        {
            observed.TrySetResult();
            return Task.FromResult("https://example.invalid/worker-published");
        };
        using var worker = ActivatorUtilities.CreateInstance<SocialPosterSchedulingWorker>(host.Provider);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.StopAsync(stop.Token);
        }
        var result = await host.ReadAsync(post.Id);
        Assert.Equal(ScheduleState.Completed, result.State);
        Assert.Equal(DeliveryState.Succeeded, Assert.Single(result.Outcomes).State);
        Assert.Single(host.Publisher.Calls);
    }

    [Theory]
    [InlineData("poll")]
    [InlineData("timeout")]
    [InlineData("lease")]
    [InlineData("lateness")]
    [InlineData("delay")]
    [InlineData("attempts-low")]
    [InlineData("attempts-high")]
    public void Registration_InvalidPolicy_RejectsOptions(string invalid)
    {
        var services = new ServiceCollection();
        services.AddSocialPosterScheduling(o =>
        {
            switch (invalid)
            {
                case "poll": o.PollInterval = TimeSpan.Zero; break;
                case "timeout": o.AttemptTimeout = TimeSpan.Zero; break;
                case "lease": o.LeaseDuration = o.AttemptTimeout; break;
                case "lateness": o.MaxLateness = TimeSpan.Zero; break;
                case "delay": o.RetryDelay = TimeSpan.Zero; break;
                case "attempts-low": o.MaxAttempts = 0; break;
                case "attempts-high": o.MaxAttempts = 11; break;
            }
        });
        using var provider = services.BuildServiceProvider();
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SchedulingOptions>>().Value);
    }
}
