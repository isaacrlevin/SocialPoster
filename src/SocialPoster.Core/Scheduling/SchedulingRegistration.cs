using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SocialPoster.Core.Scheduling;

/// <summary>Scheduling is opt-in and never changes Data Protection or initializes host storage implicitly.</summary>
public static class SchedulingRegistration
{
    /// <summary>Registers the scheduling module and default SQLite/file/protected-settings adapters, but no hosted service.</summary>
    public static IServiceCollection AddSocialPosterScheduling(this IServiceCollection services,
        Action<SchedulingOptions>? configure = null)
    {
        var options = services.AddOptions<SchedulingOptions>()
            .Validate(o => o.PollInterval > TimeSpan.Zero && o.AttemptTimeout > TimeSpan.Zero
                                                          && o.LeaseDuration > o.AttemptTimeout &&
                                                          o.MaxLateness > TimeSpan.Zero
                                                          && o.RetryDelay > TimeSpan.Zero &&
                                                          o.MaxAttempts is >= 1 and <= 10
                                                          && o.AttemptTimeout.TotalMilliseconds <= uint.MaxValue - 1,
                "Scheduling intervals must be positive, lease must exceed attempt timeout, and MaxAttempts must be 1–10.")
            .ValidateOnStart();
        if (configure is not null) options.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IScheduledPostStore, SqliteScheduledPostStore>();
        services.TryAddScoped<IScheduledMediaStore, FileScheduledMediaStore>();
        services.TryAddScoped<IScheduledCredentialProvider, LocalScheduledCredentials>();
        services.TryAddScoped<IScheduledPlatformPublisher, ScheduledPlatformPublisher>();
        services.TryAddSingleton<SchedulerSettings>();
        services.TryAddScoped<SchedulingService>();
        services.TryAddScoped<ScheduledPostExecutor>();
        return services;
    }

    /// <summary>Opt-in process polling for standalone hosts or continuously running ACA workers. Requires scheduling registration.</summary>
    public static IServiceCollection AddSocialPosterSchedulingWorker(this IServiceCollection services)
    {
        services.AddHostedService<SocialPosterSchedulingWorker>();
        return services;
    }
}

/// <summary>Creates a fresh service scope for each poll; expected storage failures are logged and retried next poll.</summary>
public sealed class SocialPosterSchedulingWorker(
    IServiceScopeFactory scopes,
    IOptions<SchedulingOptions> options,
    ILogger<SocialPosterSchedulingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<SchedulerSettings>().IsEnabledAsync(stoppingToken))
                    await scope.ServiceProvider.GetRequiredService<ScheduledPostExecutor>()
                        .RunDueAsync(ct: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or IOException
                                           or Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                logger.LogWarning(
                    "Scheduling poll failed with {FailureType}; persisted leases will recover on a later poll.",
                    ex.GetType().Name);
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}