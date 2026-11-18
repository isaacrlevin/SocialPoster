using System.Text.Json;
using SocialPoster.Core.Services;

namespace SocialPoster.Components;

/// <summary>Persists completed conflict suggestions without retaining stale contact snapshots or scores.</summary>
public sealed class ConflictResultsStore(SecretStore secrets, ContactService contacts)
{
    private const string Key = "ui.contactConflicts";
    // Reason is kept for compatibility with scans saved before likelihood scores existed.
    private sealed record SavedPair(int LeftId, int RightId, string? Reason = null);

    /// <summary>Saves completed suggestions, or clears the saved scan when dismissed.</summary>
    /// <param name="results">Suggestions to retain; null clears the saved scan.</param>
    /// <returns>A task representing the save.</returns>
    public Task SaveAsync(IReadOnlyList<ContactConflict>? results) =>
        secrets.SetAsync(Key, results is null ? null : JsonSerializer.Serialize(
            results.Select(c => new SavedPair(c.Left.Id, c.Right.Id)).ToArray()));

    /// <summary>Restores suggestions with current contacts, re-scored from their current identities. Omits deleted or
    /// merged-away rows, pairs marked "Not a match" and pairs with no remaining evidence. Does not scan.</summary>
    /// <returns>The saved suggestions (highest likelihood first), or null if no scan was saved.</returns>
    public async Task<IReadOnlyList<ContactConflict>?> LoadAsync()
    {
        var json = await secrets.GetAsync(Key);
        if (json is null) return null;
        var pairs = JsonSerializer.Deserialize<SavedPair[]>(json)
            ?? throw new InvalidOperationException("The saved conflict results are invalid.");
        var current = (await contacts.ListAsync()).ToDictionary(c => c.Id);
        var notDuplicates = await contacts.ListNotDuplicatesAsync();
        return pairs.Where(p => p.LeftId != p.RightId && current.ContainsKey(p.LeftId) && current.ContainsKey(p.RightId) &&
                !notDuplicates.Contains(ContactService.PairKey(p.LeftId, p.RightId)))
            .Select(p => new ContactConflict(current[p.LeftId], current[p.RightId],
                ContactService.ScoreDuplicate(current[p.LeftId], current[p.RightId])))
            .Where(c => c.Likelihood.HasSignal)
            .OrderByDescending(c => c.Score).ToList();
    }
}
