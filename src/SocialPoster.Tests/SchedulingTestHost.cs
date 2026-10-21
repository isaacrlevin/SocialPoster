using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocialPoster.Core;
using SocialPoster.Core.Data;
using SocialPoster.Core.Scheduling;

namespace SocialPoster.Tests;

// Each test owns a database, media directory, protection keys and deterministic clock.
internal sealed class SchedulingTestHost : IAsyncDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"socialposter-scheduling-{Guid.NewGuid():N}");
    public SchedulingTestClock Clock { get; } = new();
    public SchedulingTestCredentials Credentials { get; } = new();
    public SchedulingTestPublisher Publisher { get; } = new();
    private readonly EphemeralDataProtectionProvider protection = new();
    public IDataProtectionProvider Protection => protection;
    private readonly bool localCredentials;
    public ServiceProvider Provider { get; private set; } = null!;
    private AsyncServiceScope scope;
    public IServiceProvider Services => scope.ServiceProvider;
    public IScheduledPostStore Store => Services.GetRequiredService<IScheduledPostStore>();
    public IScheduledMediaStore Media => Services.GetRequiredService<IScheduledMediaStore>();
    public SchedulingService Scheduler => Services.GetRequiredService<SchedulingService>();
    public ScheduledPostExecutor Executor => Services.GetRequiredService<ScheduledPostExecutor>();
    public IDbContextFactory<AppDbContext> Factory => Services.GetRequiredService<IDbContextFactory<AppDbContext>>();

    private SchedulingTestHost(bool localCredentials) => this.localCredentials = localCredentials;

    public static async Task<SchedulingTestHost> CreateAsync(bool localCredentials = false)
    {
        var host = new SchedulingTestHost(localCredentials);
        await host.StartAsync();
        return host;
    }

    private async Task StartAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(protection);
        services.AddSocialPosterCore(o => o.DataDirectory = DirectoryPath);
        services.AddSocialPosterScheduling(o =>
        {
            o.AttemptTimeout = TimeSpan.FromMinutes(1);
            o.LeaseDuration = TimeSpan.FromMinutes(2);
            o.RetryDelay = TimeSpan.FromMinutes(5);
            o.MaxAttempts = 3;
            o.MaxLateness = TimeSpan.FromHours(24);
        });
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
        services.Replace(ServiceDescriptor.Singleton<IScheduledPlatformPublisher>(Publisher));
        if (!localCredentials)
            services.Replace(ServiceDescriptor.Singleton<IScheduledCredentialProvider>(Credentials));
        Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await Provider.InitializeSocialPosterAsync();
        scope = Provider.CreateAsyncScope();
    }

    public async Task RestartAsync()
    {
        await scope.DisposeAsync();
        await Provider.DisposeAsync();
        await StartAsync();
    }

    public ScheduleRequest Request(string text = "Scheduled hello", params Platform[] platforms) =>
        new(text, (platforms.Length == 0 ? [Platform.Bluesky] : platforms).ToDictionary(p => p, _ => text),
            [], Clock.GetUtcNow().AddMinutes(10).UtcDateTime, "UTC");

    public void MakeDue(ScheduledPost post) => Clock.Advance(post.DueAtUtc - Clock.GetUtcNow());

    public async Task<MediaAttachment> UploadAsync(string alt = "A descriptive image")
    {
        var path = Path.Combine(DirectoryPath, $"upload-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        return new() { FileName = "image.png", Kind = MediaKind.Image, ContentType = "image/png", Size = 4, Path = path, AltText = alt };
    }

    public async Task<ScheduledPost> ReadAsync(string id) =>
        await Store.GetAsync(id) ?? throw new InvalidOperationException("Test schedule missing.");

    public async ValueTask DisposeAsync()
    {
        if (Provider is not null)
        {
            await scope.DisposeAsync();
            await Provider.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}

internal sealed class SchedulingTestCredentials : IScheduledCredentialProvider
{
    public string Reference { get; set; } = "test-account-v1";
    public Dictionary<string, string?> Settings { get; } = [];
    public Task<ScheduledCredentials> ReadAsync(Platform platform, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ScheduledCredentials(Reference, new Dictionary<string, string?>(Settings)));
    }
}

internal sealed class SchedulingTestPublisher : IScheduledPlatformPublisher
{
    public record Call(ScheduledPlatform Platform, ScheduledCredentials Credentials, IReadOnlyList<MediaAttachment> Media);
    public List<Call> Calls { get; } = [];
    public Func<Call, CancellationToken, Task<string>> Behavior { get; set; } =
        (call, _) => Task.FromResult($"https://example.invalid/{call.Platform.Platform}/published");

    public Task<string> PublishAsync(ScheduledPlatform platform, ScheduledCredentials credentials,
        IReadOnlyList<MediaAttachment> media, CancellationToken ct = default)
    {
        var call = new Call(platform, credentials, media);
        lock (Calls) Calls.Add(call);
        return Behavior(call, ct);
    }
}

// No wall-clock sleeps: advancing this provider fires CancellationTokenSource timers too.
internal sealed class SchedulingTestClock : TimeProvider
{
    private DateTimeOffset now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly List<TestTimer> timers = [];
    public override DateTimeOffset GetUtcNow() => now;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => now.UtcTicks;

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delta));
        now += delta;
        foreach (var timer in timers.ToArray()) timer.Fire(now);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TestTimer(this, callback, state);
        timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class TestTimer(SchedulingTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? due;
        private TimeSpan period;
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
        {
            if (disposed) return false;
            due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
            period = newPeriod;
            return true;
        }
        public void Fire(DateTimeOffset instant)
        {
            if (disposed || due is null || due > instant) return;
            due = period > TimeSpan.Zero ? instant + period : null;
            callback(state);
        }
        public void Dispose() { disposed = true; clock.timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
