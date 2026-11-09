using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SocialPoster.Core;
using SocialPoster.Core.Data;

namespace SocialPoster.Core.Services;

public enum CandidateKind
{
    Saved,
    Bluesky,
    XLookup,
    AgentSearch,
    Mastodon,
}

public sealed record MentionCandidate(CandidateKind Kind, string Title, string Subtitle, string? AvatarUrl, Contact? Contact = null, BskyActor? Actor = null, string? XUsername = null, MastodonAccount? MastodonAccount = null);

/// <param name="AutoLinked">Linked by the identical-name, same-username rule rather than hard identity evidence.</param>
public sealed record EnsureResult(Contact Contact, bool Created, IReadOnlyList<string> Notes, bool NeedsReview = false, bool AutoLinked = false);

/// <summary>Counts for a completed import. Skipped identities and conflicting hints need manual review.</summary>
/// <param name="Added">Newly saved contacts.</param>
/// <param name="Updated">Imports linked to saved contacts, including auto-linked ones.</param>
/// <param name="Skipped">Ambiguous or conflicting primary identities not imported.</param>
/// <param name="NeedsReview">Imports whose bio hints could not safely be applied.</param>
/// <param name="Notes">Up to five review notes identifying affected accounts.</param>
/// <param name="AutoLinked">Updates linked by identical name and the same username on another platform.</param>
/// <param name="AutoLinkedNames">Up to five auto-linked contacts, for spot-checking.</param>
public sealed record FollowImportResult(int Added, int Updated, int Skipped, int NeedsReview, IReadOnlyList<string>? Notes = null,
    int AutoLinked = 0, IReadOnlyList<string>? AutoLinkedNames = null)
{
    /// <summary>Formats counts and review notes for standard import feedback.</summary>
    /// <returns>A human-readable import summary.</returns>
    public override string ToString() => $"{Added} added, {Updated} updated" +
        (AutoLinked > 0 ? $" ({AutoLinked} auto-linked by matching name and username)" : "") +
        $", {Skipped} skipped (ambiguous/conflicting identities), {NeedsReview} with conflicting bio links." +
        (AutoLinked > 0 ? $" Auto-linked: {string.Join(", ", AutoLinkedNames ?? [])}{(AutoLinked > (AutoLinkedNames?.Count ?? 0) ? ", …" : "")}." : "") +
        (Skipped + NeedsReview > 0 ? " Review manually in Contacts. " + string.Join(" ", Notes ?? []) : "");
}

public enum ContactSort
{
    NameAsc,
    NameDesc,
    MostUsed,
    RecentlyUsed,
    RecentlyAdded,
}

/// <summary>Contact filters and paging. LinkedPlatforms matches any listed platform; null leaves platform identities unrestricted.</summary>
public sealed record ContactQuery(string? Filter = null, bool? HasX = null, bool? HasLinkedIn = null, ContactSort Sort = ContactSort.NameAsc, int Page = 1, int PageSize = 25, IReadOnlyCollection<Platform>? Platforms = null, IReadOnlyCollection<Platform>? LinkedPlatforms = null);

public sealed record ContactPage(IReadOnlyList<Contact> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

public sealed partial class ContactService(IDbContextFactory<AppDbContext> dbFactory, BlueskyClient bluesky, XClient x, ILogger<ContactService> logger, MastodonClient? mastodon = null)
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex KeyRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,15}$")]
    private static partial Regex XUsernameRegex();

    public static bool LooksLikeXUsername(string? q) => q is not null && XUsernameRegex().IsMatch(q.TrimStart('@'));

    public async Task<List<Contact>> ListAsync(string? filter = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Filter(db.Contacts.AsNoTracking(), filter)
            .OrderBy(c => c.DisplayName)
            .ToListAsync(ct);
    }

    /// <summary>Paged, sorted, filtered contact listing for the Contacts page.</summary>
    public async Task<ContactPage> ListAsync(ContactQuery query, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = Filter(db.Contacts.AsNoTracking(), query.Filter, query.Platforms);

        if (query.LinkedPlatforms is not null)
        {
            var selected = new PlatformSelection(query.LinkedPlatforms);
            var enabled = query.Platforms is null ? PlatformSelection.All : new PlatformSelection(query.Platforms);
            var showX = selected.Contains(Platform.X) && enabled.Contains(Platform.X);
            var showBluesky = selected.Contains(Platform.Bluesky) && enabled.Contains(Platform.Bluesky);
            var showLinkedIn = selected.Contains(Platform.LinkedIn) && enabled.Contains(Platform.LinkedIn);
            var showMastodon = selected.Contains(Platform.Mastodon) && enabled.Contains(Platform.Mastodon);
            q = q.Where(c =>
                (showX && c.XUsername != null && c.XUsername.Trim() != "") ||
                (showBluesky && c.BlueskyHandle != null && c.BlueskyHandle.Trim() != "") ||
                (showMastodon && c.MastodonAcct != null && c.MastodonAcct.Trim() != "") ||
                (showLinkedIn && ((c.LinkedInProfileUrl != null && c.LinkedInProfileUrl.Trim() != "") ||
                    (c.LinkedInUrn != null && c.LinkedInUrn.Trim() != ""))));
        }

        q = query.HasX switch
        {
            true => q.Where(c => c.XUsername != null),
            false => q.Where(c => c.XUsername == null),
            null => q,
        };
        q = query.HasLinkedIn switch
        {
            true => q.Where(c => c.LinkedInProfileUrl != null || c.LinkedInUrn != null),
            false => q.Where(c => c.LinkedInProfileUrl == null && c.LinkedInUrn == null),
            null => q,
        };

        q = query.Sort switch
        {
            ContactSort.NameDesc => q.OrderByDescending(c => c.DisplayName),
            ContactSort.MostUsed => q.OrderByDescending(c => c.UseCount).ThenBy(c => c.DisplayName),
            ContactSort.RecentlyUsed => q.OrderByDescending(c => c.LastUsedAtUtc).ThenBy(c => c.DisplayName),
            ContactSort.RecentlyAdded => q.OrderByDescending(c => c.CreatedAtUtc).ThenBy(c => c.DisplayName),
            _ => q.OrderBy(c => c.DisplayName),
        };

        var total = await q.CountAsync(ct);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var page = Math.Clamp(query.Page, 1, pageCount);
        var items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new ContactPage(items, total, page, pageSize);
    }

    public async Task<List<Contact>> SearchSavedAsync(string query, int take = 6, CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Filter(db.Contacts.AsNoTracking(), query, platforms)
            .OrderByDescending(c => c.UseCount)
            .ThenBy(c => c.DisplayName)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <summary>Searches saved contacts first, then Bluesky live; offers an X lookup when the query looks like an X handle.</summary>
    public async Task<List<MentionCandidate>> SearchAsync(string query, bool xAvailable, CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null)
    {
        var selection = platforms is null ? PlatformSelection.All : new PlatformSelection(platforms);
        var candidates = new List<MentionCandidate>();
        var savedTask = SearchSavedAsync(query, 6, ct, platforms);
        var bskyTask = selection.Contains(Platform.Bluesky) ? SafeBlueskySearchAsync(query, ct) : Task.FromResult<IReadOnlyList<BskyActor>>([]);
        await Task.WhenAll(savedTask, bskyTask);

        var saved = savedTask.Result;
        candidates.AddRange(saved.Select(c => new MentionCandidate(CandidateKind.Saved, c.DisplayName, Describe(c, platforms), c.AvatarUrl, Contact: c)));

        var actors = bskyTask.Result;
        if (actors.Count > 0)
        {
            var dids = actors.Select(a => a.Did).ToList();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var known = await db.Contacts.AsNoTracking().Where(c => c.BlueskyDid != null && dids.Contains(c.BlueskyDid)).ToListAsync(ct);
            foreach (var actor in actors)
            {
                var existing = known.FirstOrDefault(k => k.BlueskyDid == actor.Did);
                if (existing is not null)
                {
                    if (candidates.All(c => c.Contact?.Id != existing.Id))
                    {
                        candidates.Add(new MentionCandidate(CandidateKind.Saved, existing.DisplayName, Describe(existing, platforms), existing.AvatarUrl, Contact: existing));
                    }

                    continue;
                }

                candidates.Add(new MentionCandidate(CandidateKind.Bluesky,
                    string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.Handle : actor.DisplayName!,
                    $"🦋 @{actor.Handle}", actor.Avatar, Actor: actor));
            }
        }

        if (selection.Contains(Platform.X) && xAvailable && LooksLikeXUsername(query) &&
            !saved.Any(c => string.Equals(c.XUsername, query.TrimStart('@'), StringComparison.OrdinalIgnoreCase)))
        {
            var u = query.TrimStart('@');
            candidates.Add(new MentionCandidate(CandidateKind.XLookup, $"Look up @{u} on X", "Uses 1 X API request", null, XUsername: u));
        }

        if (selection.Contains(Platform.Mastodon) && mastodon is not null && await mastodon.IsConfiguredAsync())
        {
            // Let lookup/auth/rate-limit errors reach the composer feedback rather than pretending there are no accounts.
            foreach (var account in await mastodon.SearchAsync(query, ct))
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var existing = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c =>
                    c.MastodonUri == account.Uri || c.MastodonAcct == account.Acct, ct);
                if (existing is not null)
                {
                    if (candidates.All(c => c.Contact?.Id != existing.Id))
                        candidates.Add(new(CandidateKind.Saved, existing.DisplayName, Describe(existing, platforms), existing.AvatarUrl, Contact: existing));
                }
                else candidates.Add(new(CandidateKind.Mastodon,
                    string.IsNullOrWhiteSpace(account.DisplayName) ? account.Acct : account.DisplayName,
                    $"Mastodon @{account.Acct}", account.Avatar, MastodonAccount: account));
            }
        }

        return candidates;
    }

    public async Task<EnsureResult?> EnsureAsync(MentionCandidate candidate, CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null) => candidate.Kind switch
    {
        CandidateKind.Saved => new EnsureResult(candidate.Contact!, false, []),
        CandidateKind.Bluesky => await EnsureFromBlueskyAsync(candidate.Actor!, ContactSource.BlueskySearch, ct, platforms),
        CandidateKind.XLookup => await EnsureFromXAsync(candidate.XUsername!, ct, platforms),
        CandidateKind.Mastodon => await EnsureFromMastodonAsync(candidate.MastodonAccount!, ContactSource.MastodonSearch, ct, platforms),
        _ => null,
    };

    public Task<EnsureResult> EnsureFromMastodonAsync(MastodonAccount account, ContactSource source,
        CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null, ContactImportReviewer? reviewer = null) =>
        EnsureImportedAsync(new Contact
        {
            DisplayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Acct : account.DisplayName.Trim(),
            MastodonAcct = MastodonIdentity.NormalizeAcct(account.Acct),
            MastodonUri = account.Uri,
            MastodonProfileUrl = account.Url,
            AvatarUrl = account.Avatar,
            Bio = account.Note,
            Source = source,
        }, Platform.Mastodon, platforms, ct, reviewer);

    public async Task<FollowImportResult> SyncMastodonFollowsAsync(IProgress<int>? progress = null,
        CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null, ContactImportReviewer? reviewer = null)
    {
        if (platforms is not null && !new PlatformSelection(platforms).Contains(Platform.Mastodon) ||
            mastodon is null || !await mastodon.IsConfiguredAsync())
            throw new InvalidOperationException("Enable and connect Mastodon under Accounts before importing follows.");
        return await SyncFollowsAsync(mastodon.GetFollowsAsync(ct),
            a => EnsureFromMastodonAsync(a, ContactSource.MastodonFollows, ct, platforms, reviewer), progress, ct);
    }

    public async Task<EnsureResult> EnsureFromBlueskyAsync(BskyActor actor, ContactSource source, CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null, ContactImportReviewer? reviewer = null)
    {
        var profile = actor.Description is null ? await SafeGetProfileAsync(actor.Did, ct) : actor;
        var contact = new Contact
        {
            DisplayName = string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.Handle : actor.DisplayName!.Trim(),
            BlueskyHandle = actor.Handle,
            BlueskyDid = actor.Did,
            AvatarUrl = actor.Avatar,
            Bio = profile?.Description,
            Source = source,
        };

        return await EnsureImportedAsync(contact, Platform.Bluesky, platforms, ct, reviewer);
    }

    public async Task<EnsureResult?> EnsureFromXAsync(string username, CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null)
    {
        username = username.Trim().TrimStart('@');
        var user = await x.LookupUsernameAsync(username, ct);
        if (user is null)
        {
            return null;
        }

        return await EnsureFromXAsync(user, ContactSource.XLookup, ct, platforms);
    }

    /// <summary>Imports a resolved X identity without an additional API lookup.</summary>
    /// <param name="user">The account returned by the X API.</param>
    /// <param name="source">Where the import originated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="platforms">Enabled platforms for cross-platform hints.</param>
    /// <returns>The saved contact and any manual-review notes.</returns>
    public Task<EnsureResult> EnsureFromXAsync(XUser user, ContactSource source, CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null, ContactImportReviewer? reviewer = null)
    {
        var contact = new Contact
        {
            DisplayName = string.IsNullOrWhiteSpace(user.Name) ? user.Username : user.Name.Trim(),
            XUsername = user.Username,
            XUserId = user.Id,
            AvatarUrl = user.ProfileImageUrl,
            Bio = user.ProfileText,
            Source = source,
        };
        return EnsureImportedAsync(contact, Platform.X, platforms, ct, reviewer);
    }

    public Task<FollowImportResult> SyncBlueskyFollowsAsync(string actor, IProgress<int>? progress = null, CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null, ContactImportReviewer? reviewer = null) =>
        SyncFollowsAsync(bluesky.GetFollowsAsync(actor, ct),
            follow => EnsureFromBlueskyAsync(follow, ContactSource.BlueskyFollows, ct, platforms, reviewer), progress, ct);

    /// <summary>Imports the authenticated X account's follows, only when X API imports are enabled.</summary>
    /// <param name="progress">Receives the number of accounts processed, including skipped accounts.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="platforms">Enabled platforms; X must be enabled.</param>
    /// <returns>Counts and bounded notes for saved and skipped accounts.</returns>
    public async Task<FollowImportResult> SyncXFollowsAsync(IProgress<int>? progress = null, CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null, ContactImportReviewer? reviewer = null)
    {
        if (!await x.CanImportFollowsAsync(platforms))
            throw new InvalidOperationException("Enable X and configure its API credentials with browser posting turned off in Accounts to import follows.");
        return await SyncFollowsAsync(x.GetFollowsAsync(ct),
            follow => EnsureFromXAsync(follow, ContactSource.XFollows, ct, platforms, reviewer), progress, ct);
    }

    private static async Task<FollowImportResult> SyncFollowsAsync<T>(IAsyncEnumerable<IReadOnlyList<T>> pages,
        Func<T, Task<EnsureResult>> ensure, IProgress<int>? progress, CancellationToken ct)
    {
        int added = 0, updated = 0, skipped = 0, review = 0, autoLinked = 0, seen = 0;
        var notes = new List<string>();
        var autoLinkedNames = new List<string>();
        await foreach (var page in pages.WithCancellation(ct))
        {
            foreach (var follow in page)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = await ensure(follow);
                    if (result.Created) added++; else updated++;
                    if (result.AutoLinked && ++autoLinked <= 5) autoLinkedNames.Add(result.Contact.DisplayName);
                    if (result.NeedsReview)
                    {
                        review++;
                        if (notes.Count < 5) notes.Add($"{result.Contact.DisplayName}: {string.Join(" ", result.Notes)}");
                    }
                }
                catch (ContactImportConflictException ex)
                {
                    skipped++;
                    if (notes.Count < 5) notes.Add(ex.Message);
                }
                progress?.Report(++seen);
            }
        }

        return new(added, updated, skipped, review, notes, autoLinked, autoLinkedNames);
    }

    public async Task<Dictionary<string, Contact>> GetByKeysAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        var list = keys.Select(k => k.ToLowerInvariant()).Distinct().ToList();
        if (list.Count == 0)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var found = await db.Contacts.AsNoTracking().Where(c => list.Contains(c.Key.ToLower())).ToListAsync(ct);
        var result = found.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var aliases = await db.ContactKeyAliases.AsNoTracking().Where(a => list.Contains(a.Key.ToLower())).ToListAsync(ct);
        var ids = aliases.Select(a => a.ContactId).ToList();
        var targets = await db.Contacts.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        foreach (var alias in aliases)
            if (targets.TryGetValue(alias.ContactId, out var target)) result[alias.Key] = target;
        return result;
    }

    public async Task MarkUsedAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        var list = keys.Select(k => k.ToLowerInvariant()).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var aliasIds = db.ContactKeyAliases.Where(a => list.Contains(a.Key.ToLower())).Select(a => a.ContactId);
        // Resolve and increment in one statement so a concurrent merge cannot lose a use of a source key.
        await db.Contacts.Where(c => list.Contains(c.Key.ToLower()) || aliasIds.Contains(c.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.UseCount, c => c.UseCount + 1)
                .SetProperty(c => c.LastUsedAtUtc, DateTime.UtcNow), ct);
    }

    /// <returns>An error message, or null on success.</returns>
    public async Task<string?> SaveAsync(Contact contact, CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null)
    {
        var selection = platforms is null ? PlatformSelection.All : new PlatformSelection(platforms);
        contact.Key = contact.Key.Trim();
        contact.DisplayName = contact.DisplayName.Trim();
        if (selection.Contains(Platform.X)) contact.XUsername = Normalize(contact.XUsername)?.TrimStart('@');
        if (selection.Contains(Platform.Bluesky)) contact.BlueskyHandle = Normalize(contact.BlueskyHandle)?.TrimStart('@').ToLowerInvariant();
        if (selection.Contains(Platform.Threads))
        {
            contact.ThreadsUsername = Normalize(contact.ThreadsUsername)?.TrimStart('@');
            if (contact.ThreadsUsername is not null && !contact.ThreadsUsername.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_'))
                return "Threads usernames contain only letters, digits, '.' and '_'.";
        }
        if (selection.Contains(Platform.Mastodon))
        {
            var raw = Normalize(contact.MastodonAcct);
            contact.MastodonAcct = MastodonIdentity.NormalizeAcct(raw);
            if (raw is not null && contact.MastodonAcct is null) return "Mastodon acct must be fully qualified: user@server.";
        }
        if (selection.Contains(Platform.LinkedIn))
        {
            contact.LinkedInUrn = Normalize(contact.LinkedInUrn);
            contact.LinkedInName = Normalize(contact.LinkedInName);
            contact.LinkedInProfileUrl = Normalize(contact.LinkedInProfileUrl);
        }

        if (!KeyRegex().IsMatch(contact.Key)) return "Key must start with a letter or digit and contain only letters, digits, '.', '_' or '-'.";
        if (contact.DisplayName.Length == 0) return "Display name is required.";
        if (selection.Contains(Platform.X) && contact.XUsername is not null && !XUsernameRegex().IsMatch(contact.XUsername)) return "X usernames are 1–15 letters, digits or underscores.";
        if (selection.Contains(Platform.LinkedIn) && contact.LinkedInUrn is not null && !LinkedInText.IsValidMentionUrn(contact.LinkedInUrn)) return "LinkedIn URN must look like urn:li:person:… or urn:li:organization:….";

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Contacts.AnyAsync(c => c.Id != contact.Id && c.Key == contact.Key, ct)) return $"The key '{contact.Key}' is already used by another contact.";
        if (await db.ContactKeyAliases.AnyAsync(a => a.Key == contact.Key, ct)) return $"The key '{contact.Key}' is reserved by a merged contact.";

        var original = contact.Id == 0 ? null : await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contact.Id, ct);
        if (contact.Id != 0 && (original is null || original.DiscoveryRevision != contact.DiscoveryRevision))
            return "This contact changed during maintenance. Reopen it before saving.";
        if (selection.Contains(Platform.Mastodon))
        {
            if (contact.MastodonAcct is null)
                contact.MastodonUri = contact.MastodonProfileUrl = null;
            else if (!SameHandle(original?.MastodonAcct, contact.MastodonAcct, Platform.Mastodon))
            {
                contact.MastodonUri = null;
                contact.MastodonProfileUrl = MastodonIdentity.ProfileUrl(contact.MastodonAcct);
                if (mastodon is not null && await mastodon.IsConfiguredAsync())
                {
                    try
                    {
                        var account = await mastodon.LookupAsync(contact.MastodonAcct, ct);
                        contact.MastodonAcct = account.Acct;
                        contact.MastodonUri = account.Uri;
                        contact.MastodonProfileUrl = account.Url;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
                    { return ex.Message; }
                }
            }
        }
        var handleChanged = !string.Equals(original?.BlueskyHandle, contact.BlueskyHandle, StringComparison.OrdinalIgnoreCase);
        if (selection.Contains(Platform.Bluesky))
        {
            if (contact.BlueskyHandle is null)
            {
                contact.BlueskyDid = null;
            }
            else if (handleChanged || string.IsNullOrWhiteSpace(contact.BlueskyDid))
            {
                contact.BlueskyDid = await SafeResolveAsync(contact.BlueskyHandle, ct);
                if (contact.BlueskyDid is null) return $"Couldn't find the Bluesky handle @{contact.BlueskyHandle}.";
            }
        }

        if (selection.Contains(Platform.X) && !string.Equals(original?.XUsername, contact.XUsername, StringComparison.OrdinalIgnoreCase))
        {
            contact.XUserId = null;
            contact.XVerifiedByCopilotAtUtc = null;
        }

        if (selection.Contains(Platform.LinkedIn) && !string.Equals(original?.LinkedInProfileUrl, contact.LinkedInProfileUrl, StringComparison.OrdinalIgnoreCase))
        {
            contact.LinkedInVerifiedByCopilotAtUtc = null;
        }

        if (!HasAnyAccount(contact))
        {
            if (contact.Id != 0) await DeleteAsync(contact.Id, ct);
            return null;
        }
        if (contact.Id == 0) db.Contacts.Add(contact); else db.Contacts.Update(contact);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return "This contact changed during maintenance. Reopen it before saving."; }
        return null;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await db.Contacts.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        await db.AccountSearches.Where(s => s.ContactId == id).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<Contact?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    /// <summary>Creates a contact from a name typed in the composer, to be filled in by an account search.</summary>
    public async Task<Contact> CreateFromNameAsync(string name, CancellationToken ct = default)
    {
        name = name.Trim().TrimStart('@');
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var baseKey = Slugs.Create(name);
        var contact = new Contact
        {
            DisplayName = name,
            Key = await UniqueKeyAsync(db, string.IsNullOrEmpty(baseKey) ? "contact" : baseKey, ct),
            Source = ContactSource.AgentSearch,
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync(ct);
        return contact;
    }

    /// <summary>Links a found account to a contact and returns the updated contact.</summary>
    public async Task<Contact?> ApplyCandidateAsync(int contactId, AccountCandidate candidate, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var contact = await db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId, ct);
        if (contact is null) return null;

        ApplyCandidate(contact, candidate);
        await db.SaveChangesAsync(ct);
        return contact;
    }

    /// <summary>Applies a candidate to an in-memory contact (used by the contact editor before saving).</summary>
    public static void ApplyCandidate(Contact contact, AccountCandidate candidate)
    {
        var cleanName = CleanProfileName(candidate.Name);
        if (candidate.Platform == Platform.X)
        {
            if (!string.Equals(contact.XUsername, candidate.Handle, StringComparison.OrdinalIgnoreCase))
            {
                contact.XUserId = null;
                contact.XVerifiedByCopilotAtUtc = null;
            }

            contact.XUsername = candidate.Handle;
        }
        else
        {
            if (!string.Equals(contact.LinkedInProfileUrl, candidate.Url, StringComparison.OrdinalIgnoreCase))
            {
                contact.LinkedInVerifiedByCopilotAtUtc = null;
            }

            contact.LinkedInProfileUrl = candidate.Url;
            if (cleanName is not null) contact.LinkedInName = cleanName;
            contact.LinkedInUrn = candidate.LinkedInUrn ?? (contact.LinkedInUrn?.StartsWith("urn:li:person:", StringComparison.Ordinal) == true ? contact.LinkedInUrn : null);
        }

        // Contacts created from a bare "@name" get a proper display name from the first account picked.
        if (contact.Source == ContactSource.AgentSearch && cleanName is not null &&
            contact.DisplayName.Length > 0 && !contact.DisplayName.Contains(' ') && cleanName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.Equals(contact.DisplayName, StringComparison.OrdinalIgnoreCase)))
        {
            contact.DisplayName = cleanName;
        }
    }

    /// <summary>Search results often read "Jane Doe - CEO - Contoso | LinkedIn"; keep just the name.</summary>
    internal static string? CleanProfileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var cut = name.IndexOfAny(['|', '–', '—', '(']);
        var dash = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0 && (cut < 0 || dash < cut)) cut = dash;
        var clean = (cut > 0 ? name[..cut] : name).Trim().TrimEnd('-', ',').Trim();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>Re-reads the Bluesky bio and applies any links to X or LinkedIn it contains.</summary>
    public async Task<IReadOnlyList<string>> RefreshHintsAsync(Contact contact, CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null)
    {
        if (contact.BlueskyDid is not null && await SafeGetProfileAsync(contact.BlueskyDid, ct) is { } profile)
        {
            contact.Bio = profile.Description;
            contact.AvatarUrl = profile.Avatar ?? contact.AvatarUrl;
        }

        var notes = ApplyBioHints(contact, platforms);
        return notes.Count > 0 ? notes : ["No links for enabled platforms found in their bio."];
    }

    public static string Describe(Contact c, IReadOnlyCollection<Platform>? platforms = null)
    {
        var selection = platforms is null ? PlatformSelection.All : new PlatformSelection(platforms);
        var parts = new List<string>();
        if (selection.Contains(Platform.Bluesky) && c.BlueskyHandle is not null) parts.Add($"🦋 @{c.BlueskyHandle}");
        if (selection.Contains(Platform.X) && c.XUsername is not null) parts.Add($"𝕏 @{c.XUsername}");
        if (selection.Contains(Platform.Mastodon) && c.MastodonAcct is not null) parts.Add($"Mastodon @{c.MastodonAcct}");
        if (selection.Contains(Platform.LinkedIn))
        {
            if (c.LinkedInUrn is not null) parts.Add("in ✓");
            else if (c.LinkedInProfileUrl is not null) parts.Add("in profile (tag by hand)");
        }
        return parts.Count == 0 ? $"{{@{c.Key}}}" : string.Join("  ·  ", parts);
    }

    private static List<string> ApplyBioHints(Contact contact, IReadOnlyCollection<Platform>? platforms = null)
    {
        var selection = platforms is null ? PlatformSelection.All : new PlatformSelection(platforms);
        var notes = new List<string>();
        var hints = ProfileLinkExtractor.Extract(contact.Bio);
        if (selection.Contains(Platform.Mastodon) && contact.MastodonAcct is null && hints.MastodonAcct is not null)
        {
            contact.MastodonAcct = hints.MastodonAcct;
            contact.MastodonProfileUrl = MastodonIdentity.ProfileUrl(hints.MastodonAcct);
            notes.Add($"Found Mastodon @{hints.MastodonAcct} in their bio.");
        }
        if (selection.Contains(Platform.X) && contact.XUsername is null && hints.XUsername is not null)
        {
            contact.XUsername = hints.XUsername;
            notes.Add($"Linked X @{hints.XUsername} from their bio.");
        }

        if (selection.Contains(Platform.LinkedIn) && contact.LinkedInProfileUrl is null && hints.LinkedInProfileUrl is not null)
        {
            contact.LinkedInProfileUrl = hints.LinkedInProfileUrl;
            notes.Add("Found their LinkedIn profile in their bio. Add their URN in Contacts to tag them.");
        }
        else if (selection.Contains(Platform.LinkedIn) && contact.LinkedInProfileUrl is null && hints.LinkedInCompanyVanity is not null)
        {
            contact.LinkedInProfileUrl = $"https://www.linkedin.com/company/{hints.LinkedInCompanyVanity}";
            notes.Add("Found a LinkedIn company page in their bio. Use Contacts ▸ Look up to link it.");
        }

        return notes;
    }

    private static IQueryable<Contact> Filter(IQueryable<Contact> q, string? filter, IReadOnlyCollection<Platform>? platforms = null)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return q;
        }

        var f = $"%{filter.Trim().TrimStart('@')}%";
        var selection = platforms is null ? PlatformSelection.All : new PlatformSelection(platforms);
        var bskyEnabled = selection.Contains(Platform.Bluesky);
        var xEnabled = selection.Contains(Platform.X);
        var liEnabled = selection.Contains(Platform.LinkedIn);
        var mastodonEnabled = selection.Contains(Platform.Mastodon);
        return q.Where(c =>
            EF.Functions.Like(c.Key, f) ||
            EF.Functions.Like(c.DisplayName, f) ||
            (bskyEnabled && c.BlueskyHandle != null && EF.Functions.Like(c.BlueskyHandle, f)) ||
            (xEnabled && c.XUsername != null && EF.Functions.Like(c.XUsername, f)) ||
            (mastodonEnabled && c.MastodonAcct != null && EF.Functions.Like(c.MastodonAcct, f)) ||
            (liEnabled && c.LinkedInName != null && EF.Functions.Like(c.LinkedInName, f)));
    }

    private static async Task<string> UniqueKeyAsync(AppDbContext db, string baseKey, CancellationToken ct)
    {
        var taken = (await db.Contacts.Where(c => c.Key.StartsWith(baseKey)).Select(c => c.Key.ToLower()).ToListAsync(ct)).ToHashSet();
        taken.UnionWith(await db.ContactKeyAliases.Where(a => a.Key.StartsWith(baseKey)).Select(a => a.Key.ToLower()).ToListAsync(ct));
        return Slugs.MakeUnique(baseKey, k => taken.Contains(k.ToLowerInvariant()));
    }

    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<IReadOnlyList<BskyActor>> SafeBlueskySearchAsync(string q, CancellationToken ct)
    {
        try { return await bluesky.SearchAsync(q, 6, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Bluesky search failed"); return []; }
    }

    private async Task<BskyActor?> SafeGetProfileAsync(string actor, CancellationToken ct)
    {
        try { return await bluesky.GetProfileAsync(actor, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Bluesky profile lookup failed"); return null; }
    }

    private async Task<string?> SafeResolveAsync(string handle, CancellationToken ct)
    {
        try { return await bluesky.ResolveHandleAsync(handle, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Bluesky resolve failed"); return null; }
    }
}
