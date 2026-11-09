using System.Diagnostics;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SocialPoster.Components.Pages;
using SocialPoster.Components.Shared;
using SocialPoster.Core;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

public class DuplicateLikelihoodTests
{
    [Fact]
    public void Shared_platform_ids_score_highest_but_never_100()
    {
        var score = ContactService.ScoreDuplicate(
            new Contact { DisplayName = "A", XUserId = "42", XUsername = "a" },
            new Contact { DisplayName = "B", XUserId = "42", XUsername = "a", BlueskyDid = "did:plc:x" });
        Assert.InRange(score.Score, 97, 99);
        Assert.Contains(score.Evidence, e => e.Description == "Same X user ID");
        Assert.Equal("Very likely", score.Band);
    }

    [Fact]
    public void Independent_signals_compound_and_conflicting_accounts_lower_the_score()
    {
        var nameOnly = ContactService.ScoreDuplicate(new Contact { DisplayName = "Jane Doe" }, new Contact { DisplayName = "jane doe" });
        var nameAndUsername = ContactService.ScoreDuplicate(
            new Contact { DisplayName = "Jane Doe", XUsername = "janedoe42" },
            new Contact { DisplayName = "Jane Doe", BlueskyHandle = "janedoe42.bsky.social" });
        var conflicting = ContactService.ScoreDuplicate(
            new Contact { DisplayName = "Jane Doe", XUserId = "1", XUsername = "one" },
            new Contact { DisplayName = "Jane Doe", XUserId = "2", XUsername = "two" });
        Assert.Equal(45, nameOnly.Score);
        Assert.True(nameAndUsername.Score > nameOnly.Score);
        Assert.Contains(nameAndUsername.Evidence, e => e.Description.Contains("on X and Bluesky"));
        Assert.True(conflicting.Score < nameOnly.Score);
        Assert.Contains(conflicting.Evidence, e => e.Points < 0 && e.Description.Contains("Different X accounts"));
        Assert.InRange(conflicting.Score, 1, 99);
    }

    [Theory]
    [InlineData("Jane Doerr", "Jane Doerk", true)]
    [InlineData("Product Team 1", "Product Team 2", false)]
    [InlineData("Podcast 2024", "Podcast 2025", false)]
    public void Names_one_edit_apart_match_unless_the_difference_is_a_digit(string a, string b, bool similar)
    {
        var score = ContactService.ScoreDuplicate(new Contact { DisplayName = a }, new Contact { DisplayName = b });
        Assert.Equal(similar, score.HasSignal);
    }

    [Fact]
    public void Unrelated_contacts_have_no_signal()
    {
        var score = ContactService.ScoreDuplicate(
            new Contact { DisplayName = "Alice Smith", XUsername = "alice" },
            new Contact { DisplayName = "Robert Jones", XUsername = "bob" });
        Assert.Equal(0, score.Score);
        Assert.False(score.HasSignal);
    }

    [Fact]
    public async Task Indexed_scan_finds_exactly_the_pairs_an_all_pairs_comparison_would()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var random = new Random(1234);
        string[] first = ["Jane", "Jame", "John", "Alex", "Alexa", "Sam", "Chris", "Pat"];
        string[] last = ["Doe", "Smith", "Smyth", "Lee", "Nguyen", "Garcia"];
        string Pick(string[] values) => values[random.Next(values.Length)];
        var contacts = Enumerable.Range(0, 300).Select(i =>
        {
            var handle = $"{Pick(first)}{Pick(last)}{random.Next(4)}".ToLowerInvariant();
            return new Contact
            {
                Key = $"c{i}",
                DisplayName = random.Next(5) == 0 ? $"{Pick(first)}{Pick(last)}".ToLowerInvariant() : $"{Pick(first)} {Pick(last)}",
                XUsername = random.Next(3) == 0 ? handle : null,
                XUserId = random.Next(10) == 0 ? $"id{random.Next(20)}" : null,
                BlueskyHandle = random.Next(3) == 0 ? $"{handle}.bsky.social" : null,
                MastodonAcct = random.Next(6) == 0 ? $"{handle}@social.example" : null,
                LinkedInProfileUrl = random.Next(8) == 0 ? $"https://www.linkedin.com/in/{handle}" : null,
                Bio = random.Next(6) == 0 ? $"Also https://bsky.app/profile/{Pick(first).ToLowerInvariant()}{Pick(last).ToLowerInvariant()}{random.Next(4)}.bsky.social" : null,
            };
        }).ToList();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(contacts);
            await db.SaveChangesAsync();
        }
        var service = host.Provider.GetRequiredService<ContactService>();
        var saved = await service.ListAsync();
        var expected = new HashSet<(int, int)>();
        for (var i = 0; i < saved.Count; i++)
            for (var j = i + 1; j < saved.Count; j++)
                if (ContactService.ScoreDuplicate(saved[i], saved[j]).HasSignal)
                    expected.Add(Ordered(saved[i].Id, saved[j].Id));

        var found = await service.FindConflictsAsync();
        var actual = found.Select(c => Ordered(c.Left.Id, c.Right.Id)).ToList();
        Assert.Equal(actual.Count, actual.Distinct().Count());
        Assert.True(expected.Count > 50, "The generated data should produce plenty of suggestions.");
        Assert.Equal(expected.OrderBy(p => p), actual.OrderBy(p => p));
        Assert.True(found.Zip(found.Skip(1)).All(p => p.First.Score >= p.Second.Score));
        static (int, int) Ordered(int a, int b) => a < b ? (a, b) : (b, a);
    }

    [Fact]
    public async Task Scan_of_twenty_thousand_contacts_is_fast()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(Enumerable.Range(0, 20_000).Select(i => new Contact
            {
                Key = $"p{i}", DisplayName = $"Person Number {i}", XUsername = $"person{i}",
                BlueskyHandle = $"someone{i}.bsky.social", Bio = $"Find me at https://x.com/person{i}"
            }));
            db.Contacts.Add(new Contact { Key = "dupe", DisplayName = "Person Number 7", BlueskyHandle = "person7.bsky.social" });
            await db.SaveChangesAsync();
        }
        var service = host.Provider.GetRequiredService<ContactService>();
        var watch = Stopwatch.StartNew();
        var found = await service.FindConflictsAsync();
        watch.Stop();
        // An all-pairs scan here would be 200M comparisons; the indexed scan should take seconds at most.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Scan took {watch.Elapsed}.");
        Assert.Contains(found, c => (c.Left.Key == "dupe" || c.Right.Key == "dupe") && c.Reason.Contains("Identical normalized names"));
    }

    [Fact]
    public async Task Merge_dialog_shows_the_score_and_evidence()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var preview = new ContactMergePreview(
            new() { Id = 1, Key = "a", DisplayName = "Jane Doe", XUsername = "jane" },
            new() { Id = 2, Key = "b", DisplayName = "Jane Doe", XUsername = "@JANE" }, [], "", "");
        var html = WebUtility.HtmlDecode(await host.RenderDialogAsync<MergeContactsDialog>(
            new() { [nameof(MergeContactsDialog.Preview)] = preview }));
        Assert.Contains("Duplicate likelihood", html);
        Assert.Contains("Very likely", html);
        Assert.Matches(@">\+90</span>\s*<span[^>]*>Same X handle", html);
        Assert.Matches(@">\+45</span>\s*<span[^>]*>Identical normalized names", html);
        // Only linked platforms are listed up front; empty ones appear only in the collapsed details.
        Assert.DoesNotContain("Bluesky: (none)", html.Split("full identity details")[0]);
    }

    [Fact]
    public async Task Import_review_ranks_candidates_by_likelihood()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var incoming = new Contact { Id = -1, Key = "(incoming — not saved)", DisplayName = "Jane Doe", XUsername = "jane" };
        var candidates = new[]
        {
            new Contact { Id = 1, Key = "weak", DisplayName = "Jane Doe" },
            new Contact { Id = 2, Key = "strong", DisplayName = "Jane Doe", XUsername = "jane" },
        }.Select(c => new ContactMergePreview(c, incoming, [], "", "")).ToList();
        var html = WebUtility.HtmlDecode(await host.RenderDialogAsync<ImportDuplicatesDialog>(
            new() { [nameof(ImportDuplicatesDialog.Review)] = new ContactImportReview(incoming, candidates, "Review") }));
        Assert.True(html.IndexOf("Review Jane Doe (strong)", StringComparison.Ordinal) <
            html.IndexOf("Review Jane Doe (weak)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suggestions_dialog_sorts_filters_reviews_and_dismisses()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Contact Make(int id, string name, string? x = null) => new() { Id = id, Key = $"k{id}", DisplayName = name, XUsername = x };
        var strong = new ContactConflict(Make(1, "A", "same"), Make(2, "B", "same"),
            ContactService.ScoreDuplicate(Make(1, "A", "same"), Make(2, "B", "same")));
        var weak = new ContactConflict(Make(3, "Jane Doe"), Make(4, "Jane Doe"),
            ContactService.ScoreDuplicate(Make(3, "Jane Doe"), Make(4, "Jane Doe")));
        var reviewed = new List<ContactConflict>();
        Func<ContactConflict, Task<IReadOnlyList<ContactConflict>?>> review = c =>
        {
            reviewed.Add(c);
            return Task.FromResult<IReadOnlyList<ContactConflict>?>([weak]);
        };
        var parameters = new Dictionary<string, object?>
        {
            [nameof(SuggestedConflictsDialog.Conflicts)] = new List<ContactConflict> { weak, strong },
            [nameof(SuggestedConflictsDialog.Review)] = review,
        };
        var html = WebUtility.HtmlDecode(await host.RenderDialogAsync<SuggestedConflictsDialog>(parameters));
        Assert.True(html.IndexOf("k1", StringComparison.Ordinal) < html.IndexOf("k3", StringComparison.Ordinal), "Highest score first.");
        Assert.Contains("Showing 2 of 2", html);

        MudBlazor.DialogResult? result = null;
        await host.RenderDialogAsync<SuggestedConflictsDialog>(parameters, async dialog =>
        {
            IReadOnlyList<ContactConflict> Filtered() =>
                (IReadOnlyList<ContactConflict>)typeof(SuggestedConflictsDialog).GetField("filtered", flags)!.GetValue(dialog)!;
            Assert.Equal([strong, weak], Filtered());
            typeof(SuggestedConflictsDialog).GetMethod("SetMin", flags)!.Invoke(dialog, [DuplicateLikelihood.Likely]);
            Assert.Equal([strong], Filtered());
            await (Task)typeof(SuggestedConflictsDialog).GetMethod("ReviewAsync", flags)!.Invoke(dialog, [strong])!;
            Assert.Empty(Filtered()); // only the weak pair remains, below the threshold
            typeof(SuggestedConflictsDialog).GetMethod("Dismiss", flags)!.Invoke(dialog, null);
        }, answer => result = answer);
        Assert.Equal([strong], reviewed);
        Assert.Equal(SuggestedConflictsDialog.DismissResult, result?.Data);
    }
}
