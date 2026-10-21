using System.Text.Json;
using SocialPoster.Core;
using SocialPoster.Core.Services;

namespace SocialPoster.Components;

/// <summary>Persists the platform switches chosen on the Accounts page in the app's local settings.</summary>
public sealed class PlatformPreferences(SecretStore secrets)
{
    private const string SettingKey = "ui.disabledPlatforms";
    private readonly SemaphoreSlim gate = new(1, 1);
    private HashSet<Platform> disabled = [];
    private bool loaded;

    /// <summary>Raised after a platform preference is successfully saved.</summary>
    public event Action? Changed;

    /// <summary>Loads saved preferences once for the current UI scope.</summary>
    /// <returns>A task representing the load.</returns>
    public async Task LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (loaded) return;
            var json = await secrets.GetAsync(SettingKey);
            if (json is not null)
            {
                var values = JsonSerializer.Deserialize<Platform[]>(json)
                    ?? throw new InvalidOperationException("The saved platform selection is invalid.");
                disabled = new PlatformSelection(values).Platforms.ToHashSet();
            }
            loaded = true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Applies saved switches to the platforms allowed by the host.</summary>
    /// <param name="available">Platforms allowed by the component or host configuration.</param>
    /// <returns>The enabled subset.</returns>
    public PlatformSelection Apply(PlatformSelection available) =>
        new(available.Platforms.Where(p => !disabled.Contains(p)));

    /// <summary>Saves one platform switch without deleting its credentials or identities.</summary>
    /// <param name="platform">The platform to change.</param>
    /// <param name="enabled">Whether the platform should be enabled.</param>
    /// <returns>A task representing the save.</returns>
    public async Task SetEnabledAsync(Platform platform, bool enabled)
    {
        if (!Enum.IsDefined(platform)) throw new ArgumentOutOfRangeException(nameof(platform));
        await LoadAsync();
        await gate.WaitAsync();
        try
        {
            var updated = new HashSet<Platform>(disabled);
            if (enabled) updated.Remove(platform); else updated.Add(platform);
            await secrets.SetAsync(SettingKey, JsonSerializer.Serialize(updated.Order().ToArray()));
            disabled = updated;
        }
        finally
        {
            gate.Release();
        }
        Changed?.Invoke();
    }
}
