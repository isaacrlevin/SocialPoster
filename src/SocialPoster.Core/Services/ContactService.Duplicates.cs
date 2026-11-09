using System.Text;

namespace SocialPoster.Core.Services;

/// <summary>One piece of duplicate evidence. Positive points are signal strength (percent); negative points are penalties.</summary>
public sealed record DuplicateEvidence(string Description, int Points);

/// <summary>A 0–99 estimate that two contacts are the same person. Never 100: a score is not proof of identity.</summary>
public sealed record DuplicateLikelihood(int Score, IReadOnlyList<DuplicateEvidence> Evidence)
{
    public const int VeryLikely = 85;
    public const int Likely = 60;
    public const int Possible = 35;

    /// <summary>A short label for a score band.</summary>
    public static string BandFor(int score) => score >= VeryLikely ? "Very likely" : score >= Likely ? "Likely"
        : score >= Possible ? "Possible" : "Unlikely";

    /// <summary>The label for this score's band.</summary>
    public string Band => BandFor(Score);

    /// <summary>True when at least one positive signal links the pair.</summary>
    public bool HasSignal => Evidence.Any(e => e.Points > 0);

    /// <summary>The positive signals, strongest first, joined for display.</summary>
    public string Summary => string.Join(" · ", Evidence.Where(e => e.Points > 0).OrderByDescending(e => e.Points)
        .Select(e => e.Description));
}

public sealed partial class ContactService
{
    private static readonly Platform[] ContactPlatforms = [Platform.X, Platform.Bluesky, Platform.Mastodon, Platform.LinkedIn, Platform.Threads];
    private const double IdWeight = 0.97, HandleWeight = 0.90, BioLinkWeight = 0.80, ExactNameWeight = 0.45,
        LongExactNameWeight = 0.55, FuzzyNameWeight = 0.30, SharedUsernameWeight = 0.50, HandleNameWeight = 0.30;
    private const int DifferentIdPenalty = 25, DifferentHandlePenalty = 10, MinHandleLocal = 5, LongName = 12;

    /// <summary>Scores how likely two contacts are the same person, with the evidence used.</summary>
    public static DuplicateLikelihood ScoreDuplicate(Contact a, Contact b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Score(new DuplicateFingerprint(a), new DuplicateFingerprint(b));
    }

    /// <summary>Finds review-only suggestions using normalized identities, explicit profile evidence, similar names
    /// or shared usernames, scored by likelihood (highest first).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="progress">Receives the number of completed and total contacts checked.</param>
    /// <returns>Suggested pairs for manual review.</returns>
    public async Task<IReadOnlyList<ContactConflict>> FindConflictsAsync(CancellationToken ct = default,
        IProgress<(long Completed, long Total)>? progress = null)
    {
        var contacts = await ListAsync(ct: ct);
        return await Task.Run(() => FindConflicts(contacts, ct, progress), ct);
    }

    // Blocking: each contact is fingerprinted once (bio regexes and handle normalization are the expensive part),
    // indexed by every key that could link it to another, and only contacts sharing a key are scored.
    // That is roughly O(n · keys) instead of comparing all n²/2 pairs.
    private static IReadOnlyList<ContactConflict> FindConflicts(List<Contact> contacts, CancellationToken ct,
        IProgress<(long Completed, long Total)>? progress)
    {
        ct.ThrowIfCancellationRequested();
        long total = contacts.Count;
        progress?.Report((0, total));
        var prints = new DuplicateFingerprint[contacts.Count];
        Parallel.For(0, contacts.Count, new ParallelOptions { CancellationToken = ct },
            i => prints[i] = new DuplicateFingerprint(contacts[i]));

        var ids = new KeyIndex();
        var targets = new KeyIndex();   // the contact's own accounts
        var bios = new KeyIndex();      // accounts the contact's bio links to
        var names = new KeyIndex();     // normalized name + one-deletion variants
        var locals = new KeyIndex();    // usernames without platform decoration
        for (var i = 0; i < prints.Length; i++)
        {
            var p = prints[i];
            foreach (var key in p.Ids) ids.Add(key, i);
            foreach (var key in p.LinkTargets) targets.Add(key, i);
            foreach (var key in p.BioLinks) bios.Add(key, i);
            foreach (var key in p.NameKeys) names.Add(key, i);
            foreach (var local in p.HandleLocals.Keys) locals.Add(local, i);
        }

        // Every lookup is symmetric and only collects higher indexes, so each pair is scored once.
        var suggestions = new List<ContactConflict>();
        var candidates = new HashSet<int>();
        var reportEvery = Math.Max(1, total / 100);
        for (var i = 0; i < prints.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = prints[i];
            candidates.Clear();
            foreach (var key in p.Ids) ids.CollectAfter(key, i, candidates);
            foreach (var key in p.LinkTargets)
            {
                targets.CollectAfter(key, i, candidates);
                bios.CollectAfter(key, i, candidates);
            }
            foreach (var key in p.BioLinks) targets.CollectAfter(key, i, candidates);
            foreach (var key in p.NameKeys) names.CollectAfter(key, i, candidates);
            foreach (var local in p.HandleLocals.Keys)
            {
                locals.CollectAfter(local, i, candidates);
                names.CollectAfter(local, i, candidates);
            }
            if (p.Name is { } name) locals.CollectAfter(name, i, candidates);

            foreach (var j in candidates)
            {
                var likelihood = Score(p, prints[j]);
                if (likelihood.HasSignal) suggestions.Add(new(contacts[i], contacts[j], likelihood));
            }
            if ((i + 1) % reportEvery == 0 || i + 1 == total)
                progress?.Report((i + 1, total));
        }
        return suggestions.OrderByDescending(s => s.Score)
            .ThenBy(s => s.Left.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Left.Id).ThenBy(s => s.Right.Id).ToList();
    }

    private static DuplicateLikelihood Score(DuplicateFingerprint a, DuplicateFingerprint b)
    {
        var evidence = new List<DuplicateEvidence>();
        void Add(string description, double weight) => evidence.Add(new(description, (int)Math.Round(weight * 100)));

        foreach (var platform in ContactPlatforms)
        {
            if (a.IdFor(platform) is { } id && id == b.IdFor(platform))
                Add($"Same {Label(platform)} {IdLabel(platform)}", IdWeight);
            if (a.HandleFor(platform) is { } handle && handle == b.HandleFor(platform))
                Add($"Same {Label(platform)} {HandleLabel(platform)} {a.DisplayHandle(platform)}", HandleWeight);
        }
        foreach (var (from, to) in new[] { (a, b), (b, a) })
        {
            var linked = from.BioLinks.Where(to.LinkTargets.Contains).Select(PlatformOfKey).Distinct().ToList();
            if (linked.Count > 0)
                Add($"Bio of {from.Contact.DisplayName} links to {to.Contact.DisplayName}'s " +
                    $"{string.Join("/", linked.Select(Label))} account", BioLinkWeight);
        }
        if (a.Name is { } an && b.Name is { } bn)
        {
            if (an == bn) Add("Identical normalized names", an.Length >= LongName ? LongExactNameWeight : ExactNameWeight);
            else if (WithinOneEdit(an, bn)) Add("Names differ by one character", FuzzyNameWeight);
        }
        foreach (var (local, platform) in a.HandleLocals)
        {
            if (!b.HandleLocals.TryGetValue(local, out var other) || other == platform) continue;
            Add($"Same username \"{local}\" on {Label(platform)} and {Label(other)}", SharedUsernameWeight);
            break;
        }
        if (a.Name is { } aName && b.HandleLocals.ContainsKey(aName) || b.Name is { } bName && a.HandleLocals.ContainsKey(bName))
            Add("One contact's username matches the other's name", HandleNameWeight);

        if (evidence.Count == 0) return new(0, evidence);

        // Two different accounts on the same platform count against the pair (people rarely have two).
        foreach (var platform in ContactPlatforms)
        {
            if (a.IdFor(platform) is { } aid && b.IdFor(platform) is { } bid)
            {
                if (aid != bid) evidence.Add(new($"Different {Label(platform)} accounts ({IdLabel(platform)}s differ)", -DifferentIdPenalty));
            }
            else if (a.HandleFor(platform) is { } ah && b.HandleFor(platform) is { } bh && ah != bh)
                evidence.Add(new($"Different {Label(platform)} {HandleLabel(platform)}s", -DifferentHandlePenalty));
        }
        // Noisy-OR: independent signals compound, so two moderate signals beat one.
        var notSame = evidence.Where(e => e.Points > 0).Aggregate(1.0, (product, e) => product * (1 - e.Points / 100.0));
        var score = (int)Math.Round((1 - notSame) * 100) + evidence.Where(e => e.Points < 0).Sum(e => e.Points);
        return new(Math.Clamp(score, 1, 99), evidence);
    }

    /// <summary>The alphanumeric username part of a canonical handle, without platform decoration:
    /// <c>jane.bsky.social</c>, <c>jane@mastodon.social</c> and <c>linkedin.com/in/jane</c> all become <c>jane</c>.</summary>
    private static string? UsernameLocal(Platform platform, string? handle)
    {
        var h = handle?.Trim().TrimStart('@').ToLowerInvariant();
        if (string.IsNullOrEmpty(h)) return null;
        var raw = platform switch
        {
            Platform.Bluesky => h.StartsWith("did:", StringComparison.Ordinal) ? null : h.Split('.')[0],
            Platform.Mastodon => h.Split('@')[0],
            Platform.LinkedIn => h.IndexOf("/in/", StringComparison.Ordinal) is var at and >= 0
                ? h[(at + 4)..].Split('/', '?', '#')[0] : null,
            _ => h,
        };
        var local = raw is null ? "" : string.Concat(raw.Where(char.IsLetterOrDigit));
        return local.Length == 0 ? null : local;
    }

    private static string Label(Platform platform) => platform.ToString();
    private static string IdLabel(Platform platform) => platform switch
    {
        Platform.X => "user ID", Platform.Bluesky => "DID", Platform.Mastodon => "actor URI", _ => "URN"
    };
    private static string HandleLabel(Platform platform) => platform == Platform.LinkedIn ? "profile" : "handle";
    private static Platform PlatformOfKey(string key) => (Platform)(key[0] - '0');

    private static string CleanName(string? value) => string.Concat(AccountSearchService.CleanName(value ?? "")
        .Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static bool SimilarNames(string a, string b)
    {
        var left = CleanName(a);
        var right = CleanName(b);
        if (left.Length < 3 || right.Length < 3) return false;
        return left == right || WithinOneEdit(left, right);
    }

    // At most one edit for reasonably long names; suggestions still require explicit review.
    // Names whose digits differ ("Team 1" / "Team 2") are distinct by design, not typos.
    private static bool WithinOneEdit(string left, string right)
    {
        if (Math.Min(left.Length, right.Length) < 6 || Math.Abs(left.Length - right.Length) > 1) return false;
        if (!left.Where(char.IsDigit).SequenceEqual(right.Where(char.IsDigit))) return false;
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

    private sealed class KeyIndex
    {
        private readonly Dictionary<string, List<int>> buckets = new(StringComparer.Ordinal);

        public void Add(string key, int index)
        {
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
            if (list.Count == 0 || list[^1] != index) list.Add(index);
        }

        /// <summary>Adds indexes greater than <paramref name="index"/> (lists are ascending).</summary>
        public void CollectAfter(string key, int index, HashSet<int> into)
        {
            if (!buckets.TryGetValue(key, out var list)) return;
            for (var k = list.Count - 1; k >= 0 && list[k] > index; k--) into.Add(list[k]);
        }
    }

    /// <summary>Normalized identity keys for one contact, computed once. Keys are prefixed with the platform digit.</summary>
    private sealed class DuplicateFingerprint
    {
        private readonly Dictionary<Platform, string> ids = [];
        private readonly Dictionary<Platform, string> handles = [];

        public DuplicateFingerprint(Contact contact)
        {
            Contact = contact;
            SetId(Platform.X, contact.XUserId);
            SetId(Platform.Bluesky, contact.BlueskyDid);
            SetId(Platform.Mastodon, contact.MastodonUri);
            SetId(Platform.LinkedIn, contact.LinkedInUrn);
            SetHandle(Platform.X, CanonicalHandle(contact.XUsername, Platform.X));
            SetHandle(Platform.Bluesky, CanonicalHandle(contact.BlueskyHandle, Platform.Bluesky));
            SetHandle(Platform.Mastodon, CanonicalHandle(contact.MastodonAcct, Platform.Mastodon));
            SetHandle(Platform.LinkedIn, CanonicalProfile(contact.LinkedInProfileUrl));
            SetHandle(Platform.Threads, contact.ThreadsUsername);
            // A bio may link to a Bluesky DID rather than a handle.
            if (Fold(contact.BlueskyDid) is { } did) LinkTargets.Add(Key(Platform.Bluesky, did));

            var hints = ProfileLinkExtractor.Extract(contact.Bio);
            if (!hints.Ambiguous)
            {
                AddBio(Platform.X, CanonicalHandle(hints.XUsername, Platform.X));
                AddBio(Platform.Bluesky, CanonicalHandle(hints.BlueskyIdentity, Platform.Bluesky));
                AddBio(Platform.Mastodon, CanonicalHandle(hints.MastodonAcct, Platform.Mastodon));
                AddBio(Platform.LinkedIn, CanonicalProfile(hints.LinkedInProfileUrl
                    ?? (hints.LinkedInCompanyVanity is { } v ? $"https://www.linkedin.com/company/{v}" : null)));
            }

            var name = CleanName(contact.DisplayName);
            if (name.Length >= 3)
            {
                Name = name;
                NameKeys.Add(name);
                // Deletion neighbourhood: two names within one edit always share one of these keys (a deletion of
                // the longer name equals the shorter, or both share a deletion for a substitution). Scoring verifies.
                if (name.Length >= 6)
                    for (var k = 0; k < name.Length; k++) NameKeys.Add(name.Remove(k, 1));
            }
            foreach (var platform in ContactPlatforms) AddLocal(platform, UsernameLocal(platform, HandleFor(platform)));
        }

        public Contact Contact { get; }
        public string? Name { get; }
        public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);
        public HashSet<string> LinkTargets { get; } = new(StringComparer.Ordinal);
        public HashSet<string> BioLinks { get; } = new(StringComparer.Ordinal);
        public HashSet<string> NameKeys { get; } = new(StringComparer.Ordinal);
        /// <summary>Alphanumeric username parts, used to spot the same username on different platforms.</summary>
        public Dictionary<string, Platform> HandleLocals { get; } = new(StringComparer.Ordinal);

        public string? IdFor(Platform platform) => ids.GetValueOrDefault(platform);
        public string? HandleFor(Platform platform) => handles.GetValueOrDefault(platform);

        public string DisplayHandle(Platform platform) => platform switch
        {
            Platform.X => "@" + Contact.XUsername?.Trim().TrimStart('@'),
            Platform.Bluesky => "@" + Contact.BlueskyHandle?.Trim().TrimStart('@'),
            Platform.Mastodon => "@" + Contact.MastodonAcct?.Trim().TrimStart('@'),
            Platform.Threads => "@" + Contact.ThreadsUsername?.Trim().TrimStart('@'),
            _ => Contact.LinkedInProfileUrl?.Trim() ?? ""
        };

        // Mirrors SameId(): trimmed, ordinal.
        private void SetId(Platform platform, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            ids[platform] = value.Trim();
            Ids.Add(Key(platform, ids[platform]));
        }

        private void SetHandle(Platform platform, string? value)
        {
            if (Fold(value) is not { } folded) return;
            handles[platform] = folded;
            LinkTargets.Add(Key(platform, folded));
        }

        private void AddBio(Platform platform, string? value)
        {
            if (Fold(value) is { } folded) BioLinks.Add(Key(platform, folded));
        }

        private void AddLocal(Platform platform, string? local)
        {
            if (local is { Length: >= MinHandleLocal }) HandleLocals.TryAdd(local, platform);
        }

        // Mirrors Same(): trimmed, leading @ removed, case-insensitive.
        private static string? Fold(string? value) => string.IsNullOrWhiteSpace(value) ? null
            : value.Trim().TrimStart('@').ToLowerInvariant() is { Length: > 0 } folded ? folded : null;
        private static string Key(Platform platform, string value) => (char)('0' + (int)platform) + value;
    }
}
