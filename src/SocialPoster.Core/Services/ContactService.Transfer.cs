using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace SocialPoster.Core.Services;

/// <summary>A portable contact, without instance-local IDs, revisions or search caches.</summary>
public sealed record ContactTransferItem
{
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? AvatarUrl { get; init; }
    public string? Bio { get; init; }
    public string? BlueskyHandle { get; init; }
    public string? BlueskyDid { get; init; }
    public string? MastodonAcct { get; init; }
    public string? MastodonUri { get; init; }
    public string? MastodonProfileUrl { get; init; }
    public string? ThreadsUsername { get; init; }
    public string? XUsername { get; init; }
    public string? XUserId { get; init; }
    public DateTime? XVerifiedByCopilotAtUtc { get; init; }
    public string? LinkedInUrn { get; init; }
    public string? LinkedInName { get; init; }
    public string? LinkedInProfileUrl { get; init; }
    public DateTime? LinkedInVerifiedByCopilotAtUtc { get; init; }
    public ContactSource Source { get; init; }
    public DateTime? CreatedAtUtc { get; init; }
    public DateTime? LastUsedAtUtc { get; init; }
    public int UseCount { get; init; }
    /// <summary>Old template keys kept from merges, so drafts using them still resolve on the new instance.</summary>
    public IReadOnlyList<string>? Aliases { get; init; }
}

/// <summary>The contacts export file.</summary>
public sealed record ContactTransferFile
{
    public const string FormatName = "socialposter.contacts";
    public const int CurrentVersion = 1;

    public string Format { get; init; } = FormatName;
    public int Version { get; init; } = CurrentVersion;
    public DateTime ExportedAtUtc { get; init; }
    public IReadOnlyList<ContactTransferItem> Contacts { get; init; } = [];
}

/// <summary>Counts for a completed contacts file import.</summary>
/// <param name="Added">New contacts created.</param>
/// <param name="Updated">Saved contacts that gained identities, aliases or metadata.</param>
/// <param name="Unchanged">Entries that matched a saved contact and added nothing.</param>
/// <param name="Skipped">Invalid or ambiguous entries that were not imported.</param>
/// <param name="Notes">Details for skipped entries and identity conflicts (capped).</param>
public sealed record ContactTransferResult(int Added, int Updated, int Unchanged, int Skipped, IReadOnlyList<string> Notes)
{
    public const int MaxNotes = 10;

    public override string ToString() =>
        $"{Added} added, {Updated} updated, {Unchanged} unchanged, {Skipped} skipped." +
        (Notes.Count > 0 ? " " + string.Join(" ", Notes) : "");
}

public sealed partial class ContactService
{
    private static readonly JsonSerializerOptions TransferJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes every contact (and its merge aliases) to a portable JSON document.</summary>
    public async Task<string> ExportJsonAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var contacts = await db.Contacts.AsNoTracking().OrderBy(c => c.Key).ToListAsync(ct);
        var aliases = (await db.ContactKeyAliases.AsNoTracking().ToListAsync(ct))
            .ToLookup(a => a.ContactId, a => a.Key);
        var file = new ContactTransferFile
        {
            ExportedAtUtc = DateTime.UtcNow,
            Contacts = contacts.Select(c => new ContactTransferItem
            {
                Key = c.Key, DisplayName = c.DisplayName, AvatarUrl = c.AvatarUrl, Bio = c.Bio,
                BlueskyHandle = c.BlueskyHandle, BlueskyDid = c.BlueskyDid,
                MastodonAcct = c.MastodonAcct, MastodonUri = c.MastodonUri, MastodonProfileUrl = c.MastodonProfileUrl,
                ThreadsUsername = c.ThreadsUsername,
                XUsername = c.XUsername, XUserId = c.XUserId, XVerifiedByCopilotAtUtc = c.XVerifiedByCopilotAtUtc,
                LinkedInUrn = c.LinkedInUrn, LinkedInName = c.LinkedInName, LinkedInProfileUrl = c.LinkedInProfileUrl,
                LinkedInVerifiedByCopilotAtUtc = c.LinkedInVerifiedByCopilotAtUtc,
                Source = c.Source, CreatedAtUtc = c.CreatedAtUtc, LastUsedAtUtc = c.LastUsedAtUtc, UseCount = c.UseCount,
                Aliases = aliases[c.Id].Order(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } a ? a : null,
            }).ToList(),
        };
        return JsonSerializer.Serialize(file, TransferJson);
    }

    /// <summary>
    /// Imports an export file in one transaction. Entries are matched to saved contacts only by shared platform
    /// identity (never by name). A match gains the identities it is missing; existing identities, key and edited
    /// metadata are never overwritten. Unmatched entries are created, keeping their key when it is free.
    /// No network lookups are made.
    /// </summary>
    /// <exception cref="InvalidDataException">The document isn't a readable contacts export.</exception>
    public async Task<ContactTransferResult> ImportJsonAsync(string json, CancellationToken ct = default)
    {
        ContactTransferFile? file;
        try { file = JsonSerializer.Deserialize<ContactTransferFile>(json, TransferJson); }
        catch (JsonException ex) { throw new InvalidDataException($"This isn't a readable contacts export: {ex.Message}", ex); }
        if (file is null || file.Format != ContactTransferFile.FormatName)
            throw new InvalidDataException("This isn't a SocialPoster contacts export.");
        if (file.Version > ContactTransferFile.CurrentVersion)
            throw new InvalidDataException("This export was made by a newer version of SocialPoster. Update this instance first.");

        int added = 0, updated = 0, unchanged = 0, skipped = 0;
        var notes = new List<string>();
        void Note(string note) { if (notes.Count < ContactTransferResult.MaxNotes) notes.Add(note); }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var contacts = await db.Contacts.ToListAsync(ct);
        var taken = new HashSet<string>(contacts.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(await db.ContactKeyAliases.Select(a => a.Key).ToListAsync(ct));
        var newAliases = new List<(Contact Contact, string Key)>();
        var touched = new HashSet<Contact>();

        foreach (var item in file.Contacts ?? [])
        {
            ct.ThrowIfCancellationRequested();
            var label = string.IsNullOrWhiteSpace(item?.DisplayName) ? item?.Key : item!.DisplayName;
            string? error = null;
            if (item is null || ToContact(item, out error) is not { } incoming)
            {
                skipped++;
                Note($"Skipped {label ?? "an entry"}: {error ?? "empty entry"}.");
                continue;
            }

            var matches = contacts.Where(c => SharesIdentity(c, incoming) || Same(c.ThreadsUsername, incoming.ThreadsUsername)).ToList();
            if (matches.Count > 1)
            {
                skipped++;
                Note($"Skipped {incoming.DisplayName}: its identities match {matches.Count} saved contacts ({string.Join(", ", matches.Select(m => m.Key))}). Merge those first.");
                continue;
            }

            var aliasKeys = (item.Aliases ?? []).Select(a => a?.Trim()).Where(a => !string.IsNullOrEmpty(a) && KeyRegex().IsMatch(a!)).Cast<string>();
            if (matches.SingleOrDefault() is { } existing)
            {
                var before = Version(existing);
                var collisions = Collisions(existing, incoming);
                foreach (var platform in Enum.GetValues<Platform>().Where(p => !collisions.Contains(p)))
                    CombineIdentity(existing, incoming, platform, replace: false);
                if (collisions.Count > 0)
                    Note($"{existing.DisplayName} ({existing.Key}): kept the saved {string.Join(", ", collisions)} identity; the file had a different one.");
                existing.AvatarUrl = Nonblank(existing.AvatarUrl) ?? incoming.AvatarUrl;
                existing.Bio = Nonblank(existing.Bio) ?? incoming.Bio;
                // Re-importing the same file must not inflate usage, so take the larger count rather than the sum.
                existing.UseCount = Math.Max(existing.UseCount, incoming.UseCount);
                existing.LastUsedAtUtc = Later(existing.LastUsedAtUtc, incoming.LastUsedAtUtc);
                var aliasAdded = false;
                // The file's key (and its aliases) keep resolving to this contact, so imported drafts still work.
                foreach (var key in aliasKeys.Prepend(incoming.Key).Where(k => taken.Add(k)))
                {
                    newAliases.Add((existing, key));
                    aliasAdded = true;
                }
                if (aliasAdded || Version(existing) != before)
                {
                    existing.DiscoveryRevision++;
                    touched.Add(existing);
                    updated++;
                }
                else unchanged++;
                continue;
            }

            if (!taken.Add(incoming.Key))
            {
                var original = incoming.Key;
                incoming.Key = Slugs.MakeUnique(original, taken.Contains);
                taken.Add(incoming.Key);
                Note($"{incoming.DisplayName}: key '{original}' was already used here, so it was saved as '{incoming.Key}'.");
            }
            db.Contacts.Add(incoming);
            contacts.Add(incoming);
            foreach (var key in aliasKeys.Where(k => taken.Add(k)))
                newAliases.Add((incoming, key));
            added++;
        }

        await db.SaveChangesAsync(ct); // assigns IDs to new contacts before their aliases reference them
        foreach (var (contact, key) in newAliases)
            db.ContactKeyAliases.Add(new() { Key = key, ContactId = contact.Id });
        if (touched.Count > 0)
            foreach (var cache in await db.AccountSearches.ToListAsync(ct))
                if (touched.Any(c => BoundTo(cache, c.Id)))
                    Invalidate(cache, "Search cache invalidated by contacts import. Run a new search explicitly.");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(added, updated, unchanged, skipped, notes);
    }

    /// <summary>Validates and normalizes an imported entry the same way the contact editor does, without lookups.</summary>
    private static Contact? ToContact(ContactTransferItem item, out string? error)
    {
        error = null;
        var c = new Contact
        {
            Key = (item.Key ?? "").Trim(),
            DisplayName = (item.DisplayName ?? "").Trim(),
            AvatarUrl = Normalize(item.AvatarUrl),
            Bio = Normalize(item.Bio),
            BlueskyHandle = Normalize(item.BlueskyHandle)?.TrimStart('@').ToLowerInvariant(),
            BlueskyDid = Normalize(item.BlueskyDid),
            MastodonUri = Normalize(item.MastodonUri),
            MastodonProfileUrl = Normalize(item.MastodonProfileUrl),
            ThreadsUsername = Normalize(item.ThreadsUsername)?.TrimStart('@'),
            XUsername = Normalize(item.XUsername)?.TrimStart('@'),
            XUserId = Normalize(item.XUserId),
            XVerifiedByCopilotAtUtc = item.XVerifiedByCopilotAtUtc,
            LinkedInUrn = Normalize(item.LinkedInUrn),
            LinkedInName = Normalize(item.LinkedInName),
            LinkedInProfileUrl = Normalize(item.LinkedInProfileUrl),
            LinkedInVerifiedByCopilotAtUtc = item.LinkedInVerifiedByCopilotAtUtc,
            Source = Enum.IsDefined(item.Source) ? item.Source : ContactSource.Manual,
            CreatedAtUtc = item.CreatedAtUtc ?? DateTime.UtcNow,
            LastUsedAtUtc = item.LastUsedAtUtc,
            UseCount = Math.Max(0, item.UseCount),
        };
        var rawAcct = Normalize(item.MastodonAcct);
        c.MastodonAcct = MastodonIdentity.NormalizeAcct(rawAcct);
        // Like the editor: a Mastodon URI or profile URL without its acct isn't a usable identity.
        if (c.MastodonAcct is null && rawAcct is null) c.MastodonUri = c.MastodonProfileUrl = null;
        if (c.DisplayName.Length == 0) c.DisplayName = c.Key;
        if (c.Key.Length == 0) c.Key = Slugs.Create(c.DisplayName);

        if (c.DisplayName.Length == 0) error = "display name is required";
        else if (!KeyRegex().IsMatch(c.Key)) error = $"key '{c.Key}' isn't a valid template key";
        else if (rawAcct is not null && c.MastodonAcct is null) error = "Mastodon acct must be fully qualified (user@server)";
        else if (c.XUsername is not null && !XUsernameRegex().IsMatch(c.XUsername)) error = $"'{c.XUsername}' isn't a valid X username";
        else if (c.ThreadsUsername is not null && !c.ThreadsUsername.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_'))
            error = $"'{c.ThreadsUsername}' isn't a valid Threads username";
        else if (c.LinkedInUrn is not null && !LinkedInText.IsValidMentionUrn(c.LinkedInUrn)) error = "LinkedIn URN must look like urn:li:person:… or urn:li:organization:…";
        else if (!HasAnyAccount(c)) error = "it has no platform accounts";
        return error is null ? c : null;
    }
}
