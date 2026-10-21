using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SocialPoster.Core.Data;

namespace SocialPoster.Core.Services;

/// <summary>A suggested pair, with evidence for user review (never an automatic merge).</summary>
public sealed record ContactConflict(Contact Left, Contact Right, string Reason);

/// <summary>A merge snapshot. Platform collisions require an explicit choice of one contact's whole identity.</summary>
public sealed record ContactMergePreview(Contact Left, Contact Right, IReadOnlyList<Platform> Collisions,
    string LeftVersion, string RightVersion);

public sealed partial class ContactService
{
    /// <summary>Counts contacts whose target platform identity or verification will be removed.</summary>
    public async Task<int> CountPurgeAsync(Platform platform, CancellationToken ct = default)
    {
        ValidatePurge(platform);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await PurgeTargets(db, platform).CountAsync(ct);
    }

    /// <summary>Removes every target identity and cached candidates, deleting contacts with no remaining accounts.</summary>
    /// <returns>The number of contacts with removed identity/verification data.</returns>
    public async Task<int> PurgePlatformAsync(Platform platform, CancellationToken ct = default)
    {
        ValidatePurge(platform);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var count = await PurgeTargets(db, platform).CountAsync(ct);
        if (platform == Platform.X)
            await db.Contacts.ExecuteUpdateAsync(s => s.SetProperty(c => c.XUsername, (string?)null)
                .SetProperty(c => c.XUserId, (string?)null).SetProperty(c => c.XVerifiedByCopilotAtUtc, (DateTime?)null)
                .SetProperty(c => c.DiscoveryRevision, c => c.DiscoveryRevision + 1), ct);
        else if (platform == Platform.Bluesky)
            await db.Contacts.ExecuteUpdateAsync(s => s.SetProperty(c => c.BlueskyHandle, (string?)null)
                .SetProperty(c => c.BlueskyDid, (string?)null)
                .SetProperty(c => c.DiscoveryRevision, c => c.DiscoveryRevision + 1), ct);
        else if (platform == Platform.Mastodon)
            await db.Contacts.ExecuteUpdateAsync(s => s.SetProperty(c => c.MastodonAcct, (string?)null)
                .SetProperty(c => c.MastodonUri, (string?)null).SetProperty(c => c.MastodonProfileUrl, (string?)null)
                .SetProperty(c => c.DiscoveryRevision, c => c.DiscoveryRevision + 1), ct);
        else
            await db.Contacts.ExecuteUpdateAsync(s => s.SetProperty(c => c.LinkedInProfileUrl, (string?)null)
                .SetProperty(c => c.LinkedInUrn, (string?)null).SetProperty(c => c.LinkedInName, (string?)null)
                .SetProperty(c => c.LinkedInVerifiedByCopilotAtUtc, (DateTime?)null)
                .SetProperty(c => c.DiscoveryRevision, c => c.DiscoveryRevision + 1), ct);
        foreach (var cache in await db.AccountSearches.ToListAsync(ct))
        {
            cache.CandidatesJson = WithoutPlatforms(cache.CandidatesJson, [platform]);
            if (cache.Status == AccountSearchStatus.Running)
                Invalidate(cache, "Contact maintenance interrupted this search. Run a new search explicitly.");
        }
        await db.SaveChangesAsync(ct);
        var emptyIds = await AccountlessContacts(db).Select(c => c.Id).ToListAsync(ct);
        await db.AccountSearches.Where(s => s.ContactId != null && emptyIds.Contains(s.ContactId.Value)).ExecuteDeleteAsync(ct);
        await AccountlessContacts(db).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return count;
    }

    private static IQueryable<Contact> AccountlessContacts(AppDbContext db) => db.Contacts.Where(c =>
        (c.XUsername == null || c.XUsername.Trim() == "") &&
        (c.XUserId == null || c.XUserId.Trim() == "") &&
        (c.BlueskyHandle == null || c.BlueskyHandle.Trim() == "") &&
        (c.BlueskyDid == null || c.BlueskyDid.Trim() == "") &&
        (c.MastodonAcct == null || c.MastodonAcct.Trim() == "") &&
        (c.MastodonUri == null || c.MastodonUri.Trim() == "") &&
        (c.MastodonProfileUrl == null || c.MastodonProfileUrl.Trim() == "") &&
        (c.LinkedInProfileUrl == null || c.LinkedInProfileUrl.Trim() == "") &&
        (c.LinkedInUrn == null || c.LinkedInUrn.Trim() == "") &&
        (c.LinkedInName == null || c.LinkedInName.Trim() == ""));

    private static bool HasAnyAccount(Contact contact) =>
        new[] { contact.XUsername, contact.XUserId, contact.BlueskyHandle, contact.BlueskyDid,
            contact.LinkedInProfileUrl, contact.LinkedInUrn, contact.LinkedInName,
            contact.MastodonAcct, contact.MastodonUri, contact.MastodonProfileUrl }
        .Any(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>Finds review-only suggestions using normalized identities, explicit profile evidence, or similar names.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="progress">Receives the number of completed and total contact-pair comparisons.</param>
    /// <returns>Suggested pairs for manual review.</returns>
    public async Task<IReadOnlyList<ContactConflict>> FindConflictsAsync(CancellationToken ct = default,
        IProgress<(long Completed, long Total)>? progress = null)
    {
        var contacts = await ListAsync(ct: ct);
        return await Task.Run(() => FindConflicts(contacts, ct, progress), ct);
    }

    private static IReadOnlyList<ContactConflict> FindConflicts(List<Contact> contacts, CancellationToken ct,
        IProgress<(long Completed, long Total)>? progress)
    {
        var suggestions = new List<ContactConflict>();
        var total = (long)contacts.Count * (contacts.Count - 1) / 2;
        long completed = 0;
        var reportEvery = Math.Max(1, total / 100);
        progress?.Report((0, total));
        for (var i = 0; i < contacts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (var j = i + 1; j < contacts.Count; j++)
            {
                ct.ThrowIfCancellationRequested();
                var a = contacts[i];
                var b = contacts[j];
                var reason = SameId(a.XUserId, b.XUserId) || SameId(a.BlueskyDid, b.BlueskyDid) ||
                    SameId(a.MastodonUri, b.MastodonUri) || SameHandle(a.MastodonAcct, b.MastodonAcct, Platform.Mastodon) ||
                    SameId(a.LinkedInUrn, b.LinkedInUrn) || SameHandle(a.XUsername, b.XUsername, Platform.X) ||
                    SameHandle(a.BlueskyHandle, b.BlueskyHandle, Platform.Bluesky) || SameProfile(a.LinkedInProfileUrl, b.LinkedInProfileUrl)
                    ? "Shared normalized platform identity"
                    : MatchesHints(a, ImportHints(b.Bio, PlatformSelection.All)) || MatchesHints(b, ImportHints(a.Bio, PlatformSelection.All))
                        ? "Explicit profile link in a bio"
                        : SimilarNames(a.DisplayName, b.DisplayName) ? "Similar normalized names — not proof of identity" : null;
                if (reason is not null) suggestions.Add(new(a, b, reason));
                completed++;
                if (completed % reportEvery == 0 || completed == total)
                    progress?.Report((completed, total));
            }
        }
        return suggestions;
    }

    /// <summary>Loads fresh identities and collisions for a merge preview.</summary>
    public async Task<ContactMergePreview> PreviewMergeAsync(int leftId, int rightId, CancellationToken ct = default)
    {
        if (leftId == rightId) throw new InvalidOperationException("Choose two different contacts.");
        var left = await GetAsync(leftId, ct) ?? throw new InvalidOperationException("The first contact no longer exists.");
        var right = await GetAsync(rightId, ct) ?? throw new InvalidOperationException("The second contact no longer exists.");
        return new(left, right, Collisions(left, right), Version(left), Version(right));
    }

    /// <summary>Merges a confirmed preview transactionally, preserving the survivor's metadata and all old template keys.</summary>
    /// <param name="preview">The reviewed snapshot; changes since preview cause rejection.</param>
    /// <param name="survivorId">One of the two contacts whose key and edited metadata survive.</param>
    /// <param name="identityChoices">For each collision, the contact ID whose entire platform identity should survive.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The surviving contact.</returns>
    public async Task<Contact> MergeAsync(ContactMergePreview preview, int survivorId,
        IReadOnlyDictionary<Platform, int> identityChoices, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(identityChoices);
        if (preview.Left.Id == preview.Right.Id || survivorId != preview.Left.Id && survivorId != preview.Right.Id)
            throw new InvalidOperationException("Choose a valid survivor from this pair.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var a = await db.Contacts.FindAsync([preview.Left.Id], ct);
        var b = await db.Contacts.FindAsync([preview.Right.Id], ct);
        if (a is null || b is null || Version(a) != preview.LeftVersion || Version(b) != preview.RightVersion)
            throw new InvalidOperationException("Contacts changed since the preview. Reopen the preview before merging.");
        var collisions = Collisions(a, b);
        if (identityChoices.Any(c => !collisions.Contains(c.Key) || c.Value != a.Id && c.Value != b.Id) ||
            collisions.Any(p => !identityChoices.ContainsKey(p)))
            throw new InvalidOperationException("Choose which complete identity to keep for every platform collision.");
        var survivor = survivorId == a.Id ? a : b;
        var source = survivorId == a.Id ? b : a;
        foreach (var platform in Enum.GetValues<Platform>())
        {
            var chosen = identityChoices.TryGetValue(platform, out var id) ? (id == a.Id ? a : b) : null;
            CombineIdentity(survivor, chosen ?? source, platform, replace: chosen is not null);
        }
        survivor.UseCount = checked(survivor.UseCount + source.UseCount);
        survivor.LastUsedAtUtc = Later(survivor.LastUsedAtUtc, source.LastUsedAtUtc);
        survivor.DiscoveryRevision++;
        // Metadata (including blanks deliberately edited by the user), creation time and source stay survivor-owned.
        foreach (var alias in await db.ContactKeyAliases.Where(k => k.ContactId == source.Id).ToListAsync(ct))
            alias.ContactId = survivor.Id;
        db.ContactKeyAliases.Add(new() { Key = source.Key, ContactId = survivor.Id });

        var caches = await db.AccountSearches.ToListAsync(ct);
        var sourcePrefix = AccountSearch.KeyFor(source.Id);
        var survivorPrefix = AccountSearch.KeyFor(survivor.Id);
        var invalidatedCollisions = new HashSet<AccountSearch>();
        foreach (var cache in caches.Where(c => BoundTo(c, source.Id)).ToList())
        {
            var newKey = cache.Key == sourcePrefix || cache.Key.StartsWith(sourcePrefix + ":", StringComparison.Ordinal)
                ? survivorPrefix + cache.Key[sourcePrefix.Length..] : cache.Key;
            var target = caches.FirstOrDefault(c => c != cache && c.Key == newKey);
            if (target is not null)
            {
                Invalidate(target, "Conflicting search caches invalidated by contact merge.");
                target.ContactId = survivor.Id;
                invalidatedCollisions.Add(target);
                db.AccountSearches.Remove(cache);
            }
            else
            {
                if (newKey == cache.Key)
                {
                    cache.ContactId = survivor.Id;
                    if (collisions.Count > 0 || cache.Status == AccountSearchStatus.Running)
                        Invalidate(cache, "Search cache invalidated by contact merge. Run a new search explicitly.");
                    continue;
                }
                db.AccountSearches.Remove(cache);
                var migrated = new AccountSearch
                {
                    Key = newKey, ContactId = survivor.Id, Query = cache.Query, Status = cache.Status,
                    Error = cache.Error, CandidatesJson = cache.CandidatesJson, Model = cache.Model,
                    CreatedAtUtc = cache.CreatedAtUtc, CompletedAtUtc = cache.CompletedAtUtc
                };
                if (collisions.Count > 0 || migrated.Status == AccountSearchStatus.Running)
                    Invalidate(migrated, "Search cache invalidated by contact merge. Run a new search explicitly.");
                db.AccountSearches.Add(migrated);
            }
        }
        foreach (var cache in caches.Where(c => BoundTo(c, survivor.Id)))
        {
            cache.ContactId = survivor.Id;
            if ((collisions.Count > 0 || cache.Status == AccountSearchStatus.Running) &&
                !invalidatedCollisions.Contains(cache))
                Invalidate(cache, "Search cache invalidated by contact merge. Run a new search explicitly.");
        }
        db.Contacts.Remove(source);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return survivor;
    }

    private static bool BoundTo(AccountSearch cache, int id) => cache.ContactId == id ||
        cache.Key == AccountSearch.KeyFor(id) || cache.Key.StartsWith(AccountSearch.KeyFor(id) + ":", StringComparison.Ordinal);

    private static void ValidatePurge(Platform platform)
    {
        if (!Enum.IsDefined(platform))
            throw new ArgumentException("Only supported platform identities can be purged.", nameof(platform));
    }

    private static IQueryable<Contact> PurgeTargets(AppDbContext db, Platform p) => p switch
    {
        Platform.X => db.Contacts.Where(c => c.XUsername != null || c.XUserId != null || c.XVerifiedByCopilotAtUtc != null),
        Platform.Bluesky => db.Contacts.Where(c => c.BlueskyHandle != null || c.BlueskyDid != null),
        Platform.Mastodon => db.Contacts.Where(c => c.MastodonAcct != null || c.MastodonUri != null || c.MastodonProfileUrl != null),
        Platform.LinkedIn => db.Contacts.Where(c => c.LinkedInProfileUrl != null || c.LinkedInUrn != null ||
            c.LinkedInName != null || c.LinkedInVerifiedByCopilotAtUtc != null),
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    private static string Version(Contact contact) => JsonSerializer.Serialize(contact);
    private static DateTime? Later(DateTime? a, DateTime? b) => a is null ? b : b is null || a >= b ? a : b;
    private static string? Nonblank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static bool Different(string? a, string? b, Func<string?, string?, bool> same) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && !same(a, b);

    private static IReadOnlyList<Platform> Collisions(Contact a, Contact b)
    {
        var result = new List<Platform>();
        if (Different(a.MastodonUri, b.MastodonUri, SameId) ||
            Different(a.MastodonAcct, b.MastodonAcct, (x, y) => SameHandle(x, y, Platform.Mastodon)) ||
            Different(a.MastodonProfileUrl, b.MastodonProfileUrl, SameProfile)) result.Add(Platform.Mastodon);
        // Even equal IDs with different handles are reviewed, not silently interpreted as a verified rename.
        if (Different(a.XUserId, b.XUserId, SameId) ||
            Different(a.XUsername, b.XUsername, (x, y) => SameHandle(x, y, Platform.X))) result.Add(Platform.X);
        if (Different(a.BlueskyDid, b.BlueskyDid, SameId) ||
            Different(a.BlueskyHandle, b.BlueskyHandle, (x, y) => SameHandle(x, y, Platform.Bluesky))) result.Add(Platform.Bluesky);
        if (Different(a.LinkedInUrn, b.LinkedInUrn, SameId) ||
            Different(a.LinkedInProfileUrl, b.LinkedInProfileUrl, SameProfile) ||
            Different(a.LinkedInName, b.LinkedInName, Same)) result.Add(Platform.LinkedIn);
        return result;
    }

    private static void CombineIdentity(Contact target, Contact from, Platform platform, bool replace)
    {
        if (platform == Platform.X)
        {
            target.XUsername = replace ? from.XUsername : Nonblank(target.XUsername) ?? from.XUsername;
            target.XUserId = replace ? from.XUserId : Nonblank(target.XUserId) ?? from.XUserId;
            target.XVerifiedByCopilotAtUtc = replace ? from.XVerifiedByCopilotAtUtc : Later(target.XVerifiedByCopilotAtUtc, from.XVerifiedByCopilotAtUtc);
        }
        else if (platform == Platform.Bluesky)
        {
            target.BlueskyHandle = replace ? from.BlueskyHandle : Nonblank(target.BlueskyHandle) ?? from.BlueskyHandle;
            target.BlueskyDid = replace ? from.BlueskyDid : Nonblank(target.BlueskyDid) ?? from.BlueskyDid;
        }
        else if (platform == Platform.Mastodon)
        {
            target.MastodonAcct = replace ? from.MastodonAcct : Nonblank(target.MastodonAcct) ?? from.MastodonAcct;
            target.MastodonUri = replace ? from.MastodonUri : Nonblank(target.MastodonUri) ?? from.MastodonUri;
            target.MastodonProfileUrl = replace ? from.MastodonProfileUrl : Nonblank(target.MastodonProfileUrl) ?? from.MastodonProfileUrl;
        }
        else
        {
            target.LinkedInUrn = replace ? from.LinkedInUrn : Nonblank(target.LinkedInUrn) ?? from.LinkedInUrn;
            target.LinkedInName = replace ? from.LinkedInName : Nonblank(target.LinkedInName) ?? from.LinkedInName;
            target.LinkedInProfileUrl = replace ? from.LinkedInProfileUrl : Nonblank(target.LinkedInProfileUrl) ?? from.LinkedInProfileUrl;
            target.LinkedInVerifiedByCopilotAtUtc = replace ? from.LinkedInVerifiedByCopilotAtUtc : Later(target.LinkedInVerifiedByCopilotAtUtc, from.LinkedInVerifiedByCopilotAtUtc);
        }
    }

    private static bool SimilarNames(string a, string b)
    {
        static string Clean(string value) => string.Concat(AccountSearchService.CleanName(value)
            .Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToLowerInvariant();
        var left = Clean(a);
        var right = Clean(b);
        if (left.Length < 3 || right.Length < 3) return false;
        if (left == right) return true;
        if (Math.Min(left.Length, right.Length) < 6 || Math.Abs(left.Length - right.Length) > 1) return false;
        // At most one edit for reasonably long names; suggestions still require explicit review.
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        foreach (var c in left)
        {
            var next = new int[right.Length + 1];
            next[0] = previous[0] + 1;
            for (var j = 1; j <= right.Length; j++)
                next[j] = Math.Min(Math.Min(next[j - 1] + 1, previous[j] + 1), previous[j - 1] + (c == right[j - 1] ? 0 : 1));
            previous = next;
        }
        return previous[^1] <= 1;
    }

    private static void Invalidate(AccountSearch cache, string reason)
    {
        cache.CandidatesJson = "[]";
        cache.Status = AccountSearchStatus.Failed;
        cache.Error = reason;
        cache.CompletedAtUtc = DateTime.UtcNow;
    }

    private static string WithoutPlatforms(string json, IReadOnlyCollection<Platform> platforms)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var kept = document.RootElement.EnumerateArray().Where(item =>
            {
                var property = item.EnumerateObject().FirstOrDefault(p => p.Name.Equals("Platform", StringComparison.OrdinalIgnoreCase));
                if (property.Value.ValueKind == JsonValueKind.Undefined) return false;
                return property.Value.ValueKind == JsonValueKind.Number
                    ? !platforms.Any(p => (int)p == property.Value.GetInt32())
                    : !platforms.Any(p => p.ToString().Equals(property.Value.GetString(), StringComparison.OrdinalIgnoreCase));
            }).ToArray();
            return JsonSerializer.Serialize(kept);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return "[]"; // Unreadable cached discovery is disposable; never preserve a possible purged identity.
        }
    }
}
