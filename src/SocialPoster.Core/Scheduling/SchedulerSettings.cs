using SocialPoster.Core.Services;

namespace SocialPoster.Core.Scheduling;

/// <summary>
/// User switch for the scheduler. When disabled, the background worker stops publishing due posts and the UI hides
/// scheduling; already scheduled posts stay stored and resume if the scheduler is turned back on.
/// </summary>
public sealed class SchedulerSettings(SecretStore secrets)
{
    private const string SettingKey = "ui.schedulerDisabled";

    /// <summary>Whether scheduled posts are published. Defaults to true. Always read fresh so the worker sees changes.</summary>
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        await secrets.GetAsync(SettingKey, ct) != "true";

    /// <summary>Saves the scheduler switch.</summary>
    public Task SetEnabledAsync(bool enabled, CancellationToken ct = default) =>
        secrets.SetAsync(SettingKey, enabled ? null : "true", ct);
}
