using System.Collections.Concurrent;

namespace SocialPoster.Posting.Services;

/// <summary>Where the platform clients read (and, when connecting an account, write) credentials.</summary>
/// <remarks>
/// Implement this over your own secret storage. <see cref="InMemoryCredentialStore"/> is the default and is
/// enough when you already hold the tokens. Keys are listed in <see cref="SettingKeys"/>.
/// </remarks>
public interface ICredentialStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task<Dictionary<string, string?>> GetManyAsync(params string[] keys);

    /// <summary>Reads related credentials together so they can't be torn by a concurrent update.</summary>
    Task<Dictionary<string, string?>> GetSnapshotAsync(string[] keys, CancellationToken ct = default);

    /// <summary>Saves a value; a null or empty value removes it.</summary>
    Task SetAsync(string key, string? value, CancellationToken ct = default);

    /// <summary>Saves a related group of values together; a null or empty value removes that key.</summary>
    Task SetManyAsync(IReadOnlyDictionary<string, string?> values, CancellationToken ct = default);
}

/// <summary>Holds credentials in process memory only. Nothing is persisted or encrypted.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _values = new();
    private readonly object _gate = new();

    public InMemoryCredentialStore() { }

    /// <summary>Creates a store pre-filled with credentials, e.g. <c>{ [SettingKeys.BlueskyHandle] = "you.bsky.social" }</c>.</summary>
    public InMemoryCredentialStore(IEnumerable<KeyValuePair<string, string?>> initial) =>
        SetManyCore(initial);

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    public Task<Dictionary<string, string?>> GetManyAsync(params string[] keys) =>
        GetSnapshotAsync(keys);

    public Task<Dictionary<string, string?>> GetSnapshotAsync(string[] keys, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(keys.ToDictionary(k => k, k => _values.TryGetValue(k, out var v) ? v : null));
    }

    public Task SetAsync(string key, string? value, CancellationToken ct = default) =>
        SetManyAsync(new Dictionary<string, string?> { [key] = value }, ct);

    public Task SetManyAsync(IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        SetManyCore(values);
        return Task.CompletedTask;
    }

    private void SetManyCore(IEnumerable<KeyValuePair<string, string?>> values)
    {
        lock (_gate)
            foreach (var (key, value) in values)
            {
                if (string.IsNullOrEmpty(value)) _values.TryRemove(key, out _);
                else _values[key] = value;
            }
    }
}
