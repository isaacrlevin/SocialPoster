using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SocialPoster.Core.Data;
using SocialPoster.Core.Services;

namespace SocialPoster.Core.Scheduling;

/// <summary>
/// Reads one protected-settings snapshot and fingerprints its ciphertext (not low-entropy plaintext).
/// Any save/rotation of account configuration requires explicit schedule approval again.
/// </summary>
public sealed class LocalScheduledCredentials(IDbContextFactory<AppDbContext> factory, IDataProtectionProvider protection)
    : IScheduledCredentialProvider
{
    public async Task<ScheduledCredentials> ReadAsync(Platform platform, CancellationToken ct = default)
    {
        string[] keys = platform switch
        {
            Platform.X => [SettingKeys.XConsumerKey, SettingKeys.XConsumerSecret, SettingKeys.XAccessToken, SettingKeys.XAccessTokenSecret, SettingKeys.XPostMethod],
            Platform.Bluesky => [SettingKeys.BlueskyHandle, SettingKeys.BlueskyAppPassword, SettingKeys.BlueskyService],
            Platform.LinkedIn => [SettingKeys.LinkedInAccessToken, SettingKeys.LinkedInPersonUrn, SettingKeys.LinkedInApiVersion],
            Platform.Mastodon => [SettingKeys.MastodonInstance, SettingKeys.MastodonAccessToken],
            Platform.Threads => [SettingKeys.ThreadsUserId, SettingKeys.ThreadsAccessToken],
            _ => throw new ArgumentOutOfRangeException(nameof(platform)),
        };
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Settings.AsNoTracking().Where(s => keys.Contains(s.Key)).ToListAsync(ct);
        var protector = protection.CreateProtector("SocialPoster.Settings.v1");
        var values = keys.ToDictionary(k => k, _ => (string?)null);
        foreach (var row in rows)
        {
            try { values[row.Key] = protector.Unprotect(row.ProtectedValue); }
            catch (CryptographicException ex) { throw new InvalidOperationException("Account credentials cannot be decrypted by this host.", ex); }
        }
        var required = platform switch
        {
            Platform.X => keys[..4],
            Platform.Bluesky => keys[..2],
            Platform.LinkedIn => keys[..2],
            _ => keys,
        };
        if (platform == Platform.X && values[SettingKeys.XPostMethod] == "intent")
            throw new InvalidOperationException("X browser-intent posts cannot be scheduled. Select API mode or deselect X.");
        if (required.Any(k => string.IsNullOrWhiteSpace(values[k])))
            throw new InvalidOperationException($"{platform} account credentials are not configured.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(keys.Select(k => new { Key = k, Value = rows.SingleOrDefault(r => r.Key == k)?.ProtectedValue })))));
        return new(fingerprint, values);
    }
}
