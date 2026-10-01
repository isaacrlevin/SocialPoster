using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SocialPoster.Web.Data;

namespace SocialPoster.Web.Services;

public static class SettingKeys
{
    public const string BlueskyHandle = "bluesky.handle";
    public const string BlueskyAppPassword = "bluesky.appPassword";
    public const string BlueskyService = "bluesky.service";

    public const string XConsumerKey = "x.consumerKey";
    public const string XConsumerSecret = "x.consumerSecret";
    public const string XAccessToken = "x.accessToken";
    public const string XAccessTokenSecret = "x.accessTokenSecret";

    /// <summary>"intent" posts via a browser intent link instead of the paid X API. Anything else (or unset) uses the API.</summary>
    public const string XPostMethod = "x.postMethod";

    public const string LinkedInApiVersion = "linkedin.apiVersion";
    public const string LinkedInAccessToken = "linkedin.accessToken";
    public const string LinkedInPersonUrn = "linkedin.personUrn";
    public const string LinkedInName = "linkedin.name";

    public const string CopilotEnabled = "copilot.enabled";
    public const string CopilotAutoSearch = "copilot.autoSearch";
    public const string CopilotModel = "copilot.model";
    public const string CopilotGitHubToken = "copilot.githubToken";
}

/// <summary>Stores settings and credentials locally, encrypted at rest.</summary>
public sealed class SecretStore(IDbContextFactory<AppDbContext> dbFactory, IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("SocialPoster.Settings.v1");

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(row.ProtectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public async Task<Dictionary<string, string?>> GetManyAsync(params string[] keys)
    {
        var result = new Dictionary<string, string?>();
        foreach (var key in keys)
        {
            result[key] = await GetAsync(key);
        }

        return result;
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (string.IsNullOrEmpty(value))
        {
            if (row is not null)
            {
                db.Settings.Remove(row);
            }
        }
        else if (row is null)
        {
            db.Settings.Add(new Setting { Key = key, ProtectedValue = _protector.Protect(value) });
        }
        else
        {
            row.ProtectedValue = _protector.Protect(value);
        }

        await db.SaveChangesAsync(ct);
    }
}
