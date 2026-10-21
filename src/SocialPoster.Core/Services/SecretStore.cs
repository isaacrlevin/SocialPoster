using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SocialPoster.Core.Data;

namespace SocialPoster.Core.Services;

/// <summary>Credential keys for the optional Copilot account finder.</summary>
public static class CopilotSettingKeys
{
    public const string Enabled = "copilot.enabled";
    public const string AutoSearch = "copilot.autoSearch";
    public const string Model = "copilot.model";
    public const string GitHubToken = "copilot.githubToken";
}

/// <summary>Stores settings and credentials locally, encrypted at rest.</summary>
public sealed class SecretStore(IDbContextFactory<AppDbContext> dbFactory, IDataProtectionProvider protection) : ICredentialStore
{
    private readonly IDataProtector _protector = protection.CreateProtector("SocialPoster.Settings.v1");
    private IReadOnlyDictionary<string, string?>? _frozen;
    private Dictionary<string, string?>? _preview;

    /// <summary>Creates an isolated editable credential preview. Changes never reach persistent settings.</summary>
    public async Task<SecretStore> CreatePreviewAsync(IReadOnlyDictionary<string, string?> values)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var keys = await db.Settings.AsNoTracking().Select(s => s.Key).ToArrayAsync();
        var snapshot = await GetSnapshotAsync(keys);
        foreach (var (key, value) in values) snapshot[key] = value;
        return new SecretStore(dbFactory, protection) { _preview = snapshot };
    }


    // A private read-only instance pins a scheduled attempt to the credential snapshot it validated.
    // No AsyncLocal/global override: interactive requests and concurrent jobs cannot see this snapshot.
    internal SecretStore Freeze(IReadOnlyDictionary<string, string?> snapshot) =>
        new(dbFactory, protection) { _frozen = new Dictionary<string, string?>(snapshot) };

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        if (_preview is not null) return _preview.GetValueOrDefault(key);
        if (_frozen is not null) return _frozen.GetValueOrDefault(key);
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

    /// <summary>Reads related credentials from a single database snapshot.</summary>
    public async Task<Dictionary<string, string?>> GetSnapshotAsync(string[] keys, CancellationToken ct = default)
    {
        if (_preview is not null) return keys.ToDictionary(k => k, k => _preview.GetValueOrDefault(k));
        if (_frozen is not null) return keys.ToDictionary(k => k, k => _frozen.GetValueOrDefault(k));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Settings.AsNoTracking().Where(s => keys.Contains(s.Key)).ToListAsync(ct);
        var result = keys.ToDictionary(k => k, _ => (string?)null);
        foreach (var row in rows)
        {
            try { result[row.Key] = _protector.Unprotect(row.ProtectedValue); }
            catch (System.Security.Cryptography.CryptographicException) { result[row.Key] = null; }
        }
        return result;
    }

    /// <summary>Atomically saves a related group of protected settings.</summary>
    public async Task SetManyAsync(IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        if (_preview is not null) { foreach (var (key, value) in values) _preview[key] = value; return; }
        if (_frozen is not null) throw new InvalidOperationException("Scheduled credential snapshots are read-only.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        foreach (var (key, value) in values)
        {
            var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (string.IsNullOrEmpty(value))
            {
                if (row is not null) db.Settings.Remove(row);
            }
            else if (row is null) db.Settings.Add(new() { Key = key, ProtectedValue = _protector.Protect(value) });
            else row.ProtectedValue = _protector.Protect(value);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        if (_preview is not null) { _preview[key] = value; return; }
        if (_frozen is not null) throw new InvalidOperationException("Scheduled credential snapshots are read-only.");
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
