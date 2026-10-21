using Microsoft.EntityFrameworkCore;
using SocialPoster.Core.Data;

namespace SocialPoster.Core.Services;

internal sealed class ContactImportConflictException(string message) : InvalidOperationException(message);

public sealed partial class ContactService
{
    // No name-based inference: only stable IDs, exact handles, and explicit profile evidence.
    private async Task<EnsureResult> EnsureImportedAsync(Contact incoming, Platform platform,
        IReadOnlyCollection<Platform>? platforms, CancellationToken ct, ContactImportReviewer? reviewer = null,
        IReadOnlyDictionary<int, string>? expectedVersions = null)
    {
        if (reviewer is not null) return await ReviewImportAsync(incoming, platform, platforms, reviewer, ct);
        if (string.IsNullOrWhiteSpace(PrimaryId(incoming, platform)) || string.IsNullOrWhiteSpace(PrimaryHandle(incoming, platform)) ||
            platform == Platform.X && !LooksLikeXUsername(incoming.XUsername))
            throw new InvalidOperationException($"{platform} returned an incomplete account identity. Import stopped without saving that account.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Resolve legacy identities before taking the database write lock.
        var verifiedRenames = await VerifyPossibleRenamesAsync(incoming, platform, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var contacts = await db.Contacts.ToListAsync(ct);
        if (expectedVersions is not null) ValidateImportSnapshot(contacts, expectedVersions);
        var selection = platforms is null ? PlatformSelection.All : new PlatformSelection(platforms);
        var hints = ImportHints(incoming.Bio, selection);
        var notes = new List<string>();
        var review = hints.Ambiguous;
        if (review) notes.Add("Multiple profile links in the bio; cross-platform linking was not attempted.");
        var stable = contacts.Where(c => SameId(PrimaryId(c, platform), PrimaryId(incoming, platform))).ToList();
        var handles = contacts.Where(c => SameHandle(PrimaryHandle(c, platform), PrimaryHandle(incoming, platform), platform)).ToList();
        var matches = stable.Count > 0 ? stable : handles;
        if (matches.Count == 0)
            matches = contacts.Where(c => verifiedRenames.TryGetValue(c.Id, out var handle) &&
                SameHandle(PrimaryHandle(c, platform), handle, platform)).ToList();
        if (matches.Count == 0 && !hints.Ambiguous)
        {
            matches = contacts.Where(c => MatchesHints(c, hints) ||
                MatchesHints(incoming, ImportHints(c.Bio, selection))).ToList();
        }
        if (matches.Count > 1)
            throw new ContactImportConflictException($"Cannot safely link {incoming.DisplayName}: multiple saved contacts match its identity or profile links.");
        var existing = matches.SingleOrDefault();
        var verifiedRename = existing is not null && verifiedRenames.TryGetValue(existing.Id, out var oldHandle) &&
            SameHandle(PrimaryHandle(existing, platform), oldHandle, platform);
        if (existing is not null && (ConflictingId(PrimaryId(existing, platform), PrimaryId(incoming, platform)) ||
            (string.IsNullOrWhiteSpace(PrimaryId(existing, platform)) && !verifiedRename &&
                ConflictingHandle(PrimaryHandle(existing, platform), PrimaryHandle(incoming, platform), platform))))
            throw new ContactImportConflictException($"Cannot safely link {incoming.DisplayName}: the saved contact already has a different {platform} identity.");

        // An ID can survive a rename, but don't steal a handle attached to another saved identity.
        if (handles.Any(c => c.Id != existing?.Id))
            throw new ContactImportConflictException($"Cannot safely link {incoming.DisplayName}: the handle belongs to another saved contact.");
        var target = existing ?? incoming;
        if (existing is not null)
        {
            if (platform == Platform.X)
            {
                target.XUserId = incoming.XUserId;
                target.XUsername = incoming.XUsername;
            }
            else if (platform == Platform.Mastodon)
            {
                target.MastodonAcct = incoming.MastodonAcct;
                target.MastodonUri = incoming.MastodonUri;
                target.MastodonProfileUrl = incoming.MastodonProfileUrl;
            }
            else
            {
                target.BlueskyDid = incoming.BlueskyDid;
                target.BlueskyHandle = incoming.BlueskyHandle;
            }
            // User-owned metadata, keys, timestamps, usage and search/history references stay intact.
            target.AvatarUrl ??= incoming.AvatarUrl;
            target.Bio ??= incoming.Bio;
        }

        if (!hints.Ambiguous)
        {
            LinkHint(Platform.X, hints.XUsername, target.XUsername,
                c => SameHandle(c.XUsername, hints.XUsername, Platform.X), value => target.XUsername = value);
            LinkHint(Platform.Bluesky, hints.BlueskyIdentity, target.BlueskyDid ?? target.BlueskyHandle,
                c => Same(c.BlueskyDid, hints.BlueskyIdentity) || SameHandle(c.BlueskyHandle, hints.BlueskyIdentity, Platform.Bluesky),
                value => { if (value.StartsWith("did:", StringComparison.Ordinal)) target.BlueskyDid = value; else target.BlueskyHandle = value; });
            LinkHint(Platform.LinkedIn, hints.LinkedInProfileUrl, target.LinkedInProfileUrl,
                c => SameProfile(c.LinkedInProfileUrl, hints.LinkedInProfileUrl), value => target.LinkedInProfileUrl = value);
            LinkHint(Platform.Mastodon, hints.MastodonAcct, target.MastodonAcct,
                c => SameHandle(c.MastodonAcct, hints.MastodonAcct, Platform.Mastodon),
                value => { target.MastodonAcct = value; target.MastodonProfileUrl = MastodonIdentity.ProfileUrl(value); });
        }
        if (existing is null)
        {
            target.Key = await UniqueKeyAsync(db, Slugs.FromHandle(PrimaryHandle(incoming, platform)!), ct);
            db.Contacts.Add(target);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(target, existing is null, notes, review);

        void LinkHint(Platform hintPlatform, string? value, string? current, Func<Contact, bool> owned, Action<string> apply)
        {
            if (hintPlatform == platform || value is null) return;
            if ((current is not null && !SameProfile(current, value)) ||
                contacts.Any(c => c.Id != target.Id && owned(c)))
            {
                review = true;
                notes.Add($"Conflicting {hintPlatform} bio link was not applied; review it manually in Contacts.");
            }
            else if (current is null)
            {
                apply(value);
                notes.Add($"Linked {hintPlatform} from an explicit profile link/bio hint.");
            }
        }
    }

    private static string? PrimaryId(Contact c, Platform platform) => platform switch
    {
        Platform.X => c.XUserId,
        Platform.Bluesky => c.BlueskyDid,
        Platform.Mastodon => c.MastodonUri,
        _ => null,
    };
    private static string? PrimaryHandle(Contact c, Platform platform) => platform switch
    {
        Platform.X => c.XUsername,
        Platform.Bluesky => c.BlueskyHandle,
        Platform.Mastodon => c.MastodonAcct,
        _ => null,
    };

    private async Task<Dictionary<int, string>> VerifyPossibleRenamesAsync(Contact incoming, Platform platform, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var contacts = await db.Contacts.AsNoTracking().ToListAsync(ct);
        var matches = new Dictionary<int, string>();
        if (platform == Platform.Mastodon)
        {
            // Fully qualified acct is exact evidence; never infer federation renames from a display name.
            return matches;
        }
        if (contacts.Any(c => platform == Platform.X
            ? SameId(c.XUserId, incoming.XUserId) || SameHandle(c.XUsername, incoming.XUsername, platform)
            : SameId(c.BlueskyDid, incoming.BlueskyDid) || SameHandle(c.BlueskyHandle, incoming.BlueskyHandle, platform)))
            return matches;
        // Names only identify candidates to verify; they are never proof of identity.
        var candidates = contacts.Where(c => Same(c.DisplayName, incoming.DisplayName) &&
            (platform == Platform.X
                ? string.IsNullOrWhiteSpace(c.XUserId) && !string.IsNullOrWhiteSpace(c.XUsername) &&
                    !SameHandle(c.XUsername, incoming.XUsername, platform)
                : string.IsNullOrWhiteSpace(c.BlueskyDid) && !string.IsNullOrWhiteSpace(c.BlueskyHandle) &&
                    !SameHandle(c.BlueskyHandle, incoming.BlueskyHandle, platform)));
        foreach (var candidate in candidates)
        {
            string? resolvedId;
            if (platform == Platform.X)
            {
                if (!await x.IsConfiguredAsync())
                    throw new ContactImportConflictException($"Possible duplicate for {incoming.DisplayName}: verify the saved X account before importing a different handle.");
                var handle = CanonicalHandle(candidate.XUsername, platform);
                if (!LooksLikeXUsername(handle))
                    throw new ContactImportConflictException($"Possible duplicate for {incoming.DisplayName}: the saved X account needs manual review.");
                resolvedId = (await x.LookupUsernameAsync(handle!, ct))?.Id;
            }
            else
            {
                resolvedId = await bluesky.ResolveHandleAsync(CanonicalHandle(candidate.BlueskyHandle, platform)!, ct);
            }
            if (string.IsNullOrWhiteSpace(resolvedId))
                throw new ContactImportConflictException($"Possible duplicate for {incoming.DisplayName}: the old {platform} handle could not be verified. Review the saved contact instead of creating another.");
            if (SameId(resolvedId, platform == Platform.X ? incoming.XUserId : incoming.BlueskyDid))
                matches.Add(candidate.Id, (platform == Platform.X ? candidate.XUsername : candidate.BlueskyHandle)!);
        }
        return matches;
    }

    private static CrossPlatformHints ImportHints(string? bio, PlatformSelection selection)
    {
        var hints = ProfileLinkExtractor.Extract(bio);
        return FilterImportHints(hints, selection);
    }

    private static CrossPlatformHints FilterImportHints(CrossPlatformHints hints, PlatformSelection selection)
    {
        return hints with
        {
            XUsername = selection.Contains(Platform.X) ? hints.XUsername : null,
            MastodonAcct = selection.Contains(Platform.Mastodon) ? hints.MastodonAcct : null,
            BlueskyIdentity = selection.Contains(Platform.Bluesky) ? hints.BlueskyIdentity : null,
            LinkedInProfileUrl = selection.Contains(Platform.LinkedIn)
                ? hints.LinkedInProfileUrl ?? (hints.LinkedInCompanyVanity is { } v ? $"https://www.linkedin.com/company/{v}" : null)
                : null,
        };
    }

    private static bool MatchesHints(Contact c, CrossPlatformHints hints) => !hints.Ambiguous &&
        (SameHandle(c.XUsername, hints.XUsername, Platform.X) || SameHandle(c.BlueskyHandle, hints.BlueskyIdentity, Platform.Bluesky) ||
         Same(c.BlueskyDid, hints.BlueskyIdentity) || SameProfile(c.LinkedInProfileUrl, hints.LinkedInProfileUrl) ||
         SameHandle(c.MastodonAcct, hints.MastodonAcct, Platform.Mastodon));

    private static string? CanonicalHandle(string? value, Platform platform)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = value.Trim().TrimStart('@');
        if (platform == Platform.Mastodon) return MastodonIdentity.NormalizeAcct(clean);
        if (platform == Platform.X)
            return AccountCandidates.Normalize([new("X", null, clean, null, null, 100, null)]).SingleOrDefault()?.Handle ?? clean;
        return ProfileLinkExtractor.Extract(clean).BlueskyIdentity ?? clean;
    }

    private static bool SameHandle(string? left, string? right, Platform platform) =>
        Same(CanonicalHandle(left, platform), CanonicalHandle(right, platform));

    private static bool ConflictingHandle(string? left, string? right, Platform platform) =>
        !string.IsNullOrWhiteSpace(left) && !SameHandle(left, right, platform);

    private static bool Same(string? left, string? right) => !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) && string.Equals(left.Trim().TrimStart('@'), right.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase);

    private static bool SameProfile(string? left, string? right) => Same(CanonicalProfile(left), CanonicalProfile(right));
    private static string? CanonicalProfile(string? value) =>
        value?.Contains("linkedin.com/", StringComparison.OrdinalIgnoreCase) == true
            ? AccountCandidates.Normalize([new("LinkedIn", null, value, null, null, 100, null)]).SingleOrDefault()?.Url ?? value.TrimEnd('/')
            : value?.TrimEnd('/');
    private static bool SameId(string? left, string? right) => !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) && string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);
    private static bool ConflictingId(string? left, string? right) => !string.IsNullOrWhiteSpace(left) && !SameId(left, right);
}
