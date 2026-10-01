using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SocialPoster.Core;
using SocialPoster.Web.Data;

namespace SocialPoster.Web.Services;

public enum CandidateKind
{
    Saved,
    Bluesky,
    XLookup,
    AgentSearch,
}

public sealed record MentionCandidate(CandidateKind Kind, string Title, string Subtitle, string? AvatarUrl, Contact? Contact = null, BskyActor? Actor = null, string? XUsername = null);

public sealed record EnsureResult(Contact Contact, bool Created, IReadOnlyList<string> Notes);

public enum ContactSort
{
    NameAsc,
    NameDesc,
    MostUsed,
    RecentlyUsed,
    RecentlyAdded,
}

/// <summary>Tri-state filter: null = any, true = must have, false = must not have.</summary>
public sealed record ContactQuery(string? Filter = null, bool? HasX = null, bool? HasLinkedIn = null, ContactSort Sort = ContactSort.NameAsc, int Page = 1, int PageSize = 25);

public sealed record ContactPage(IReadOnlyList<Contact> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

public sealed partial class ContactService(IDbContextFactory<AppDbContext> dbFactory, BlueskyClient bluesky, XClient x, ILogger<ContactService> logger)
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
        var q = Filter(db.Contacts.AsNoTracking(), query.Filter);

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

    public async Task<List<Contact>> SearchSavedAsync(string query, int take = 6, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Filter(db.Contacts.AsNoTracking(), query)
            .OrderByDescending(c => c.UseCount)
            .ThenBy(c => c.DisplayName)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <summary>Searches saved contacts first, then Bluesky live; offers an X lookup when the query looks like an X handle.</summary>
    public async Task<List<MentionCandidate>> SearchAsync(string query, bool xAvailable, CancellationToken ct = default)
    {
        var candidates = new List<MentionCandidate>();
        var savedTask = SearchSavedAsync(query, 6, ct);
        var bskyTask = SafeBlueskySearchAsync(query, ct);
        await Task.WhenAll(savedTask, bskyTask);

        var saved = savedTask.Result;
        candidates.AddRange(saved.Select(c => new MentionCandidate(CandidateKind.Saved, c.DisplayName, Describe(c), c.AvatarUrl, Contact: c)));

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
                        candidates.Add(new MentionCandidate(CandidateKind.Saved, existing.DisplayName, Describe(existing), existing.AvatarUrl, Contact: existing));
                    }

                    continue;
                }

                candidates.Add(new MentionCandidate(CandidateKind.Bluesky,
                    string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.Handle : actor.DisplayName!,
                    $"🦋 @{actor.Handle}", actor.Avatar, Actor: actor));
            }
        }

        if (xAvailable && LooksLikeXUsername(query) &&
            !saved.Any(c => string.Equals(c.XUsername, query.TrimStart('@'), StringComparison.OrdinalIgnoreCase)))
        {
            var u = query.TrimStart('@');
            candidates.Add(new MentionCandidate(CandidateKind.XLookup, $"Look up @{u} on X", "Uses 1 X API request", null, XUsername: u));
        }

        return candidates;
    }

    public async Task<EnsureResult?> EnsureAsync(MentionCandidate candidate, CancellationToken ct = default) => candidate.Kind switch
    {
        CandidateKind.Saved => new EnsureResult(candidate.Contact!, false, []),
        CandidateKind.Bluesky => await EnsureFromBlueskyAsync(candidate.Actor!, ContactSource.BlueskySearch, ct),
        CandidateKind.XLookup => await EnsureFromXAsync(candidate.XUsername!, ct),
        _ => null,
    };

    public async Task<EnsureResult> EnsureFromBlueskyAsync(BskyActor actor, ContactSource source, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Contacts.FirstOrDefaultAsync(c => c.BlueskyDid == actor.Did, ct);
        if (existing is not null)
        {
            existing.BlueskyHandle = actor.Handle;
            existing.AvatarUrl = actor.Avatar ?? existing.AvatarUrl;
            await db.SaveChangesAsync(ct);
            return new EnsureResult(existing, false, []);
        }

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

        var notes = ApplyBioHints(contact);
        contact.Key = await UniqueKeyAsync(db, Slugs.FromHandle(actor.Handle), ct);
        db.Contacts.Add(contact);
        await db.SaveChangesAsync(ct);
        return new EnsureResult(contact, true, notes);
    }

    public async Task<EnsureResult?> EnsureFromXAsync(string username, CancellationToken ct = default)
    {
        username = username.TrimStart('@');
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Contacts.FirstOrDefaultAsync(c => c.XUsername == username, ct);
        if (existing is not null)
        {
            return new EnsureResult(existing, false, []);
        }

        var user = await x.LookupUsernameAsync(username, ct);
        if (user is null)
        {
            return null;
        }

        var contact = new Contact
        {
            DisplayName = user.Name,
            XUsername = user.Username,
            XUserId = user.Id,
            AvatarUrl = user.ProfileImageUrl,
            Bio = user.Description,
            Source = ContactSource.XLookup,
            Key = await UniqueKeyAsync(db, Slugs.FromHandle(user.Username), ct),
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync(ct);
        return new EnsureResult(contact, true, []);
    }

    public async Task<(int Added, int Updated)> SyncBlueskyFollowsAsync(string actor, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        int added = 0, updated = 0, seen = 0;
        await foreach (var page in bluesky.GetFollowsAsync(actor, ct))
        {
            foreach (var follow in page)
            {
                var result = await EnsureFromBlueskyAsync(follow, ContactSource.BlueskyFollows, ct);
                if (result.Created) added++; else updated++;
                progress?.Report(++seen);
            }
        }

        return (added, updated);
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
        return found.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
    }

    public async Task MarkUsedAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        var list = keys.Select(k => k.ToLowerInvariant()).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Contacts.Where(c => list.Contains(c.Key.ToLower()))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.UseCount, c => c.UseCount + 1)
                .SetProperty(c => c.LastUsedAtUtc, DateTime.UtcNow), ct);
    }

    /// <returns>An error message, or null on success.</returns>
    public async Task<string?> SaveAsync(Contact contact, CancellationToken ct = default)
    {
        contact.Key = contact.Key.Trim();
        contact.DisplayName = contact.DisplayName.Trim();
        contact.XUsername = Normalize(contact.XUsername)?.TrimStart('@');
        contact.BlueskyHandle = Normalize(contact.BlueskyHandle)?.TrimStart('@').ToLowerInvariant();
        contact.LinkedInUrn = Normalize(contact.LinkedInUrn);
        contact.LinkedInName = Normalize(contact.LinkedInName);
        contact.LinkedInProfileUrl = Normalize(contact.LinkedInProfileUrl);

        if (!KeyRegex().IsMatch(contact.Key)) return "Key must start with a letter or digit and contain only letters, digits, '.', '_' or '-'.";
        if (contact.DisplayName.Length == 0) return "Display name is required.";
        if (contact.XUsername is not null && !XUsernameRegex().IsMatch(contact.XUsername)) return "X usernames are 1–15 letters, digits or underscores.";
        if (contact.LinkedInUrn is not null && !LinkedInText.IsValidMentionUrn(contact.LinkedInUrn)) return "LinkedIn URN must look like urn:li:person:… or urn:li:organization:….";

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Contacts.AnyAsync(c => c.Id != contact.Id && c.Key == contact.Key, ct)) return $"The key '{contact.Key}' is already used by another contact.";

        var original = contact.Id == 0 ? null : await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contact.Id, ct);
        var handleChanged = !string.Equals(original?.BlueskyHandle, contact.BlueskyHandle, StringComparison.OrdinalIgnoreCase);
        if (contact.BlueskyHandle is null)
        {
            contact.BlueskyDid = null;
        }
        else if (handleChanged || string.IsNullOrWhiteSpace(contact.BlueskyDid))
        {
            contact.BlueskyDid = await SafeResolveAsync(contact.BlueskyHandle, ct);
            if (contact.BlueskyDid is null) return $"Couldn't find the Bluesky handle @{contact.BlueskyHandle}.";
        }

        if (!string.Equals(original?.XUsername, contact.XUsername, StringComparison.OrdinalIgnoreCase))
        {
            contact.XUserId = null;
            contact.XVerifiedByCopilotAtUtc = null;
        }

        if (!string.Equals(original?.LinkedInProfileUrl, contact.LinkedInProfileUrl, StringComparison.OrdinalIgnoreCase))
        {
            contact.LinkedInVerifiedByCopilotAtUtc = null;
        }

        if (contact.Id == 0) db.Contacts.Add(contact); else db.Contacts.Update(contact);
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Contacts.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        await db.AccountSearches.Where(s => s.ContactId == id).ExecuteDeleteAsync(ct);
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
    public async Task<IReadOnlyList<string>> RefreshHintsAsync(Contact contact, CancellationToken ct = default)
    {
        if (contact.BlueskyDid is not null && await SafeGetProfileAsync(contact.BlueskyDid, ct) is { } profile)
        {
            contact.Bio = profile.Description;
            contact.AvatarUrl = profile.Avatar ?? contact.AvatarUrl;
        }

        var notes = ApplyBioHints(contact);
        return notes.Count > 0 ? notes : ["No X or LinkedIn links found in their bio."];
    }

    public static string Describe(Contact c)
    {
        var parts = new List<string>();
        if (c.BlueskyHandle is not null) parts.Add($"🦋 @{c.BlueskyHandle}");
        if (c.XUsername is not null) parts.Add($"𝕏 @{c.XUsername}");
        if (c.LinkedInUrn is not null) parts.Add("in ✓");
        else if (c.LinkedInProfileUrl is not null) parts.Add("in profile (tag by hand)");
        return parts.Count == 0 ? $"{{@{c.Key}}}" : string.Join("  ·  ", parts);
    }

    private static List<string> ApplyBioHints(Contact contact)
    {
        var notes = new List<string>();
        var hints = ProfileLinkExtractor.Extract(contact.Bio);
        if (contact.XUsername is null && hints.XUsername is not null)
        {
            contact.XUsername = hints.XUsername;
            notes.Add($"Linked X @{hints.XUsername} from their bio.");
        }

        if (contact.LinkedInProfileUrl is null && hints.LinkedInProfileUrl is not null)
        {
            contact.LinkedInProfileUrl = hints.LinkedInProfileUrl;
            notes.Add("Found their LinkedIn profile in their bio. Add their URN in Contacts to tag them.");
        }
        else if (contact.LinkedInProfileUrl is null && hints.LinkedInCompanyVanity is not null)
        {
            contact.LinkedInProfileUrl = $"https://www.linkedin.com/company/{hints.LinkedInCompanyVanity}";
            notes.Add("Found a LinkedIn company page in their bio. Use Contacts ▸ Look up to link it.");
        }

        return notes;
    }

    private static IQueryable<Contact> Filter(IQueryable<Contact> q, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return q;
        }

        var f = $"%{filter.Trim().TrimStart('@')}%";
        return q.Where(c =>
            EF.Functions.Like(c.Key, f) ||
            EF.Functions.Like(c.DisplayName, f) ||
            (c.BlueskyHandle != null && EF.Functions.Like(c.BlueskyHandle, f)) ||
            (c.XUsername != null && EF.Functions.Like(c.XUsername, f)) ||
            (c.LinkedInName != null && EF.Functions.Like(c.LinkedInName, f)));
    }

    private static async Task<string> UniqueKeyAsync(AppDbContext db, string baseKey, CancellationToken ct)
    {
        var taken = (await db.Contacts.Where(c => c.Key.StartsWith(baseKey)).Select(c => c.Key.ToLower()).ToListAsync(ct)).ToHashSet();
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
