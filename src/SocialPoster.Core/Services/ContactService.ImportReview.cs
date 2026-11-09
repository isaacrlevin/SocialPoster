using System.Data;
using Microsoft.EntityFrameworkCore;

namespace SocialPoster.Core.Services;

public enum ImportReviewAction { Merge, KeepSeparate, Skip, Cancel }

/// <summary>The incoming account is transient (ID -1). Names suggest review, never identity.</summary>
public sealed record ContactImportReview(Contact Incoming, IReadOnlyList<ContactMergePreview> Candidates, string Reason);

/// <summary>Merge retains the selected saved contact's metadata. Collision choices use saved ID or -1 for incoming.</summary>
public sealed record ContactImportDecision(ImportReviewAction Action, int? ContactId = null,
    IReadOnlyDictionary<Platform, int>? IdentityChoices = null, bool Confirmed = false);

public delegate Task<ContactImportDecision> ContactImportReviewer(ContactImportReview review, CancellationToken ct);

public sealed partial class ContactService
{
    private async Task<EnsureResult> ReviewImportAsync(Contact incoming, Platform platform,
        IReadOnlyCollection<Platform>? platforms, ContactImportReviewer reviewer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(PrimaryId(incoming, platform)) ||
            string.IsNullOrWhiteSpace(PrimaryHandle(incoming, platform)) ||
            platform == Platform.X && !LooksLikeXUsername(incoming.XUsername))
            throw new InvalidOperationException($"{platform} returned an incomplete account identity.");

        var selection = platforms is null ? PlatformSelection.All : new PlatformSelection(platforms);
        var hints = ImportHints(incoming.Bio, selection);
        // Only explicit, unambiguous hints become proposed identities. They are reviewed, not saved yet.
        if (!hints.Ambiguous)
        {
            if (platform != Platform.X) incoming.XUsername = hints.XUsername;
            if (platform != Platform.Bluesky && hints.BlueskyIdentity is { } b)
            {
                if (b.StartsWith("did:", StringComparison.Ordinal)) incoming.BlueskyDid = b;
                else incoming.BlueskyHandle = b;
            }
            incoming.LinkedInProfileUrl = hints.LinkedInProfileUrl;
            if (platform != Platform.Mastodon && hints.MastodonAcct is { } m)
            {
                incoming.MastodonAcct = m;
                incoming.MastodonProfileUrl = MastodonIdentity.ProfileUrl(m);
            }
        }
        await using var read = await dbFactory.CreateDbContextAsync(ct);
        var contacts = await read.Contacts.AsNoTracking().ToListAsync(ct);
        var versions = contacts.ToDictionary(c => c.Id, Version);
        var evidence = ProfileLinkExtractor.ExtractReviewHints(incoming.Bio)
            .Select(h => FilterImportHints(h, selection)).ToList();
        var candidates = contacts.Where(c => SharesIdentity(c, incoming) ||
            evidence.Any(h => MatchesHints(c, h)) ||
            ProfileLinkExtractor.ExtractReviewHints(c.Bio).Any(h => MatchesHints(incoming, FilterImportHints(h, selection))) ||
            SimilarNames(c.DisplayName, incoming.DisplayName)).ToList();
        // A uniquely matched, unchanged primary identity is already imported. Do not prompt again on retries,
        // unless another contact owns proposed evidence or there are conflicting/ambiguous hints.
        var exact = candidates.Where(c => SameId(PrimaryId(c, platform), PrimaryId(incoming, platform)) &&
            SameHandle(PrimaryHandle(c, platform), PrimaryHandle(incoming, platform), platform)).ToList();
        if (!hints.Ambiguous && exact.Count == 1 && Collisions(exact[0], incoming).Count == 0 &&
            !contacts.Any(c => c.Id != exact[0].Id && SharesIdentity(c, incoming)))
            return await EnsureImportedAsync(incoming, platform, platforms, ct, expectedVersions: versions);
        if (candidates.Count == 0 && !hints.Ambiguous)
            return await EnsureImportedAsync(incoming, platform, platforms, ct, expectedVersions: versions);
        // The auto-link rule is the same with or without review; only prompt when other candidates compete with it.
        if (candidates.Count == 1 && FindAutoLink(contacts, incoming, platform, hints, selection)?.Contact.Id == candidates[0].Id)
            return await EnsureImportedAsync(incoming, platform, platforms, ct, expectedVersions: versions);

        incoming.Id = -1;
        incoming.Key = "(incoming — not saved)";
        var incomingVersion = Version(incoming);
        var previews = candidates.Select(c => new ContactMergePreview(c, incoming, Collisions(c, incoming),
            Version(c), incomingVersion)).ToList();
        var decision = await reviewer(new(incoming, previews,
            hints.Ambiguous ? "Ambiguous profile links were not applied. Review this account before continuing."
                : "Possible duplicate: shared identity, explicit profile evidence or similar normalized name. Names are not proof."),
            ct).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (decision.Action == ImportReviewAction.Cancel) throw new OperationCanceledException("Follow sync cancelled during duplicate review.", ct);
        if (decision.Action == ImportReviewAction.Skip)
            throw new ContactImportConflictException($"Skipped {incoming.DisplayName} during duplicate review.");
        if (Version(incoming) != incomingVersion ||
            candidates.Any(c => !versions.TryGetValue(c.Id, out var v) || v != Version(c)))
            throw new InvalidOperationException("The review preview changed. Retry the sync for a fresh preview.");
        var reviewedChoices = decision.IdentityChoices?.ToDictionary(c => c.Key, c => c.Value);

        // No write transaction spans the callback or any network request.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var current = await db.Contacts.ToListAsync(ct);
        ValidateImportSnapshot(current, versions);
        Contact target;
        bool created;
        if (decision.Action == ImportReviewAction.KeepSeparate)
        {
            target = incoming;
            target.Id = 0;
            target.Key = await UniqueKeyAsync(db, Slugs.FromHandle(PrimaryHandle(incoming, platform)!), ct);
            created = true;
        }
        else if (decision.Action == ImportReviewAction.Merge && decision.Confirmed &&
                 decision.ContactId is { } id && previews.Any(p => p.Left.Id == id))
        {
            target = current.Single(c => c.Id == id);
            var collisions = Collisions(target, incoming);
            var choices = reviewedChoices ?? new Dictionary<Platform, int>();
            if (choices.Any(c => !collisions.Contains(c.Key) || c.Value != id && c.Value != -1) ||
                collisions.Any(p => !choices.ContainsKey(p)))
                throw new InvalidOperationException("Choose a complete identity for every platform collision.");
            foreach (var p in Enum.GetValues<Platform>())
                CombineIdentity(target, choices.TryGetValue(p, out var chosen) && chosen == id ? target : incoming,
                    p, replace: choices.ContainsKey(p));
            target.DiscoveryRevision++;
            foreach (var cache in await db.AccountSearches.ToListAsync(ct))
                if (BoundTo(cache, id))
                    Invalidate(cache, "Search cache invalidated by import review. Run a new search explicitly.");
            created = false;
        }
        else throw new InvalidOperationException("Confirm a valid import decision before saving.");

        if (current.Any(c => c.Id != target.Id && SharesIdentity(c, target)))
            throw new InvalidOperationException("A selected identity belongs to another saved contact. Nothing was saved for this account; merge saved contacts first or skip it.");
        if (created) db.Contacts.Add(target);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(target, created, hints.Ambiguous ? ["Ambiguous profile links were not applied."] : [], hints.Ambiguous);
    }

    private static bool SharesIdentity(Contact a, Contact b) =>
        SameId(a.XUserId, b.XUserId) || SameHandle(a.XUsername, b.XUsername, Platform.X) ||
        SameId(a.BlueskyDid, b.BlueskyDid) || SameHandle(a.BlueskyHandle, b.BlueskyHandle, Platform.Bluesky) ||
        SameId(a.MastodonUri, b.MastodonUri) || SameHandle(a.MastodonAcct, b.MastodonAcct, Platform.Mastodon) ||
        SameProfile(a.MastodonProfileUrl, b.MastodonProfileUrl) ||
        SameId(a.LinkedInUrn, b.LinkedInUrn) || SameProfile(a.LinkedInProfileUrl, b.LinkedInProfileUrl);

    private static void ValidateImportSnapshot(List<Contact> contacts, IReadOnlyDictionary<int, string> versions)
    {
        if (contacts.Count != versions.Count || contacts.Any(c => !versions.TryGetValue(c.Id, out var v) || v != Version(c)))
            throw new InvalidOperationException("Contacts changed during duplicate review. Retry the sync for a fresh preview.");
    }
}
