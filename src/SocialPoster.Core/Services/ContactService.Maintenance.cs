using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SocialPoster.Core.Data;

namespace SocialPoster.Core.Services;

/// <summary>A suggested pair, with a likelihood score and evidence for user review (never an automatic merge).</summary>
public sealed record ContactConflict(Contact Left, Contact Right, DuplicateLikelihood Likelihood)
{
    /// <summary>The 1–99 likelihood that the pair is the same person.</summary>
    public int Score => Likelihood.Score;

    /// <summary>The positive evidence, strongest first.</summary>
    public string Reason => Likelihood.Summary;
}

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

    /// <summary>Counts contacts imported from <paramref name="platform"/> that a plain purge keeps because they still
    /// have other linked accounts — usually links inferred from the bio that platform supplied.</summary>
    public async Task<int> CountImportedSurvivorsAsync(Platform platform, CancellationToken ct = default)
    {
        ValidatePurge(platform);
        var sources = SourcesFor(platform);
        if (sources.Length == 0) return 0;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var imported = await db.Contacts.AsNoTracking().Where(c => sources.Contains(c.Source)).ToListAsync(ct);
        return imported.Count(c => { ClearIdentity(c, platform); return HasAnyAccount(c); });
    }

    /// <summary>Counts every saved contact.</summary>
    public async Task<int> CountAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Contacts.CountAsync(ct);
    }

    /// <summary>Deletes every contact with its merge aliases and contact-bound search caches. Post history is kept;
    /// mentions of deleted contacts become unknown.</summary>
    /// <returns>The number of contacts deleted.</returns>
    public async Task<int> DeleteAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await db.ContactKeyAliases.ExecuteDeleteAsync(ct);
        // Every search cache is contact-bound (key contact:{id}); none are useful without contacts.
        await db.AccountSearches.ExecuteDeleteAsync(ct);
        var count = await db.Contacts.ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return count;
    }

    /// <summary>Removes every target identity and cached candidates, deleting contacts with no remaining accounts.</summary>
    /// <returns>The number of contacts with removed identity/verification data.</returns>
    public Task<int> PurgePlatformAsync(Platform platform, CancellationToken ct = default) =>
        PurgePlatformAsync(platform, deleteImportedContacts: false, ct);

    /// <summary>Removes every target identity and cached candidates, deleting contacts with no remaining accounts.</summary>
    /// <param name="platform">The platform to purge.</param>
    /// <param name="deleteImportedContacts">Also delete contacts imported from this platform even if they still have
    /// other linked accounts, so no name, bio or bio-derived link from that platform is left behind.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of contacts with removed identity/verification data or deleted as imported.</returns>
    public async Task<int> PurgePlatformAsync(Platform platform, bool deleteImportedContacts, CancellationToken ct = default)
    {
        ValidatePurge(platform);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var affected = (await PurgeTargets(db, platform).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        var sources = SourcesFor(platform);
        if (deleteImportedContacts && sources.Length > 0)
        {
            var importedIds = await db.Contacts.Where(c => sources.Contains(c.Source)).Select(c => c.Id).ToListAsync(ct);
            affected.UnionWith(importedIds);
            await db.AccountSearches.Where(s => s.ContactId != null && importedIds.Contains(s.ContactId.Value)).ExecuteDeleteAsync(ct);
            await db.Contacts.Where(c => importedIds.Contains(c.Id)).ExecuteDeleteAsync(ct);
        }
        var count = affected.Count;
        if (platform == Platform.Threads)
            await db.Contacts.ExecuteUpdateAsync(s => s.SetProperty(c => c.ThreadsUsername, (string?)null)
                .SetProperty(c => c.DiscoveryRevision, c => c.DiscoveryRevision + 1), ct);
        else if (platform == Platform.X)
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
        else if (platform == Platform.LinkedIn)
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
        (c.LinkedInName == null || c.LinkedInName.Trim() == "") &&
        (c.ThreadsUsername == null || c.ThreadsUsername.Trim() == ""));

    private static bool HasAnyAccount(Contact contact) =>
        new[] { contact.XUsername, contact.XUserId, contact.BlueskyHandle, contact.BlueskyDid,
            contact.LinkedInProfileUrl, contact.LinkedInUrn, contact.LinkedInName,
            contact.MastodonAcct, contact.MastodonUri, contact.MastodonProfileUrl, contact.ThreadsUsername }
        .Any(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>Import sources whose name, avatar and bio came from <paramref name="platform"/>.</summary>
    private static ContactSource[] SourcesFor(Platform platform) => platform switch
    {
        Platform.X => [ContactSource.XFollows, ContactSource.XLookup],
        Platform.Bluesky => [ContactSource.BlueskyFollows, ContactSource.BlueskySearch],
        Platform.Mastodon => [ContactSource.MastodonFollows, ContactSource.MastodonSearch],
        _ => []
    };

    private static void ClearIdentity(Contact c, Platform platform)
    {
        switch (platform)
        {
            case Platform.X: c.XUsername = c.XUserId = null; break;
            case Platform.Bluesky: c.BlueskyHandle = c.BlueskyDid = null; break;
            case Platform.Mastodon: c.MastodonAcct = c.MastodonUri = c.MastodonProfileUrl = null; break;
            case Platform.LinkedIn: c.LinkedInProfileUrl = c.LinkedInUrn = c.LinkedInName = null; break;
            case Platform.Threads: c.ThreadsUsername = null; break;
        }
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
        Platform.Threads => db.Contacts.Where(c => c.ThreadsUsername != null),
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
        if (Different(a.ThreadsUsername, b.ThreadsUsername, Same)) result.Add(Platform.Threads);
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
        else if (platform == Platform.Threads)
            target.ThreadsUsername = replace ? from.ThreadsUsername : Nonblank(target.ThreadsUsername) ?? from.ThreadsUsername;
        else
        {
            target.LinkedInUrn = replace ? from.LinkedInUrn : Nonblank(target.LinkedInUrn) ?? from.LinkedInUrn;
            target.LinkedInName = replace ? from.LinkedInName : Nonblank(target.LinkedInName) ?? from.LinkedInName;
            target.LinkedInProfileUrl = replace ? from.LinkedInProfileUrl : Nonblank(target.LinkedInProfileUrl) ?? from.LinkedInProfileUrl;
            target.LinkedInVerifiedByCopilotAtUtc = replace ? from.LinkedInVerifiedByCopilotAtUtc : Later(target.LinkedInVerifiedByCopilotAtUtc, from.LinkedInVerifiedByCopilotAtUtc);
        }
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
