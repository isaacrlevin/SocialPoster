using System.Net;
using System.Text.Json;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocialPoster.Components.Pages;
using SocialPoster.Components;
using SocialPoster.Components.Shared;
using SocialPoster.Core;
using SocialPoster.Core.Data;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

public class ContactMaintenanceTests
{
    [Fact]
    public async Task Saved_conflicts_restore_after_navigation_with_fresh_contacts_and_can_be_dismissed()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var a = new Contact { Key = "a", DisplayName = "First", XUsername = "first" };
        var b = new Contact { Key = "b", DisplayName = "Second", XUsername = "@FIRST" };
        var c = new Contact { Key = "c", DisplayName = "Third", XUsername = "third" };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(a, b, c);
            await db.SaveChangesAsync();
        }
        using (var scope = host.Provider.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ConflictResultsStore>();
            Assert.Null(await store.LoadAsync());
            await store.SaveAsync([Pair(a, b), Pair(a, c)]);
        }
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            await db.Contacts.Where(contact => contact.Id == a.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(contact => contact.DisplayName, "Current name"));
        }
        await host.Provider.GetRequiredService<ContactService>().DeleteAsync(c.Id);
        using (var scope = host.Provider.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ConflictResultsStore>();
            var pair = Assert.Single((await store.LoadAsync())!);
            Assert.Equal("Current name", pair.Left.DisplayName);
            Assert.Contains("Same X handle", pair.Reason);
            Assert.Equal(90, pair.Score); // rescored from current data, not the saved snapshot
        }
        var html = await host.RenderAsync<Contacts>(null);
        // The page shows only a compact banner; the pair list lives in the suggestions dialog.
        Assert.Contains("1 possible duplicate", html);
        Assert.Contains("1 very likely", html);
        Assert.Contains("Review duplicates", html);
        Assert.DoesNotContain("Same X handle", html);
        Assert.DoesNotContain("Checking contacts for conflicts", html);
        using (var scope = host.Provider.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ConflictResultsStore>();
            await store.SaveAsync([]);
            Assert.Empty((await store.LoadAsync())!);
            await store.SaveAsync(null);
            Assert.Null(await store.LoadAsync());
        }
        Assert.DoesNotContain("possible duplicate", await host.RenderAsync<Contacts>(null));
    }

    [Fact]
    public async Task Not_a_match_hides_pair_from_scans_and_saved_results_follows_merges_and_cascades_on_delete()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var service = host.Provider.GetRequiredService<ContactService>();
        var a = new Contact { Key = "a", DisplayName = "First", XUsername = "same" };
        var b = new Contact { Key = "b", DisplayName = "Second", XUsername = "@SAME" };
        var c = new Contact { Key = "c", DisplayName = "Third", XUsername = "same" };
        var d = new Contact { Key = "d", DisplayName = "Fourth", BlueskyHandle = "d.bsky.social" };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(a, b, c, d);
            await db.SaveChangesAsync();
        }
        Assert.Equal(3, (await service.FindConflictsAsync()).Count); // a-b, a-c, b-c
        using (var scope = host.Provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ConflictResultsStore>().SaveAsync(await service.FindConflictsAsync());

        await service.MarkNotDuplicateAsync(b.Id, a.Id);
        await service.MarkNotDuplicateAsync(a.Id, b.Id); // idempotent, order-independent
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MarkNotDuplicateAsync(a.Id, a.Id));
        Assert.Equal([ContactService.PairKey(a.Id, b.Id)], await service.ListNotDuplicatesAsync());
        var remaining = await service.FindConflictsAsync();
        Assert.DoesNotContain(remaining, p => ContactService.PairKey(p.Left.Id, p.Right.Id) == ContactService.PairKey(a.Id, b.Id));
        Assert.Equal(2, remaining.Count);
        using (var scope = host.Provider.CreateScope())
            Assert.Equal(2, (await scope.ServiceProvider.GetRequiredService<ConflictResultsStore>().LoadAsync())!.Count);

        // a is merged into d: "a is not b" now means "d is not b".
        await service.MarkNotDuplicateAsync(a.Id, d.Id); // a pair the user later merges anyway is dropped
        var merge = await service.PreviewMergeAsync(a.Id, d.Id);
        await service.MergeAsync(merge, d.Id, new Dictionary<Platform, int>());
        Assert.Equal([ContactService.PairKey(b.Id, d.Id)], await service.ListNotDuplicatesAsync());
        Assert.DoesNotContain(await service.FindConflictsAsync(), p => ContactService.PairKey(p.Left.Id, p.Right.Id) == ContactService.PairKey(b.Id, d.Id));

        await service.DeleteAsync(b.Id);
        Assert.Empty(await service.ListNotDuplicatesAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MarkNotDuplicateAsync(b.Id, c.Id));
    }

    [Theory]
    [InlineData(Platform.X)]
    [InlineData(Platform.Bluesky)]
    [InlineData(Platform.LinkedIn)]
    public async Task Purge_deletes_contacts_losing_last_account_and_cleans_aliases_and_searches(Platform platform)
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var empty = new Contact { Key = "delete", DisplayName = "Delete" };
        if (platform == Platform.X) empty.XUsername = "only";
        else if (platform == Platform.Bluesky) empty.BlueskyDid = "did:plc:only";
        else empty.LinkedInProfileUrl = "https://www.linkedin.com/in/only";
        var keep = new Contact { Key = "keep", DisplayName = "Keep", LinkedInName = "Known name" };
        if (platform == Platform.LinkedIn) keep.BlueskyHandle = "keep.bsky.social";
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(empty, keep);
            await db.SaveChangesAsync();
            db.ContactKeyAliases.Add(new() { Key = "old", ContactId = empty.Id });
            db.AccountSearches.Add(new() { Key = AccountSearch.KeyFor(empty.Id), ContactId = empty.Id });
            db.PostHistory.Add(new() { Template = "{@delete}", ResultsJson = "[]" });
            await db.SaveChangesAsync();
        }
        var service = host.Provider.GetRequiredService<ContactService>();
        await service.PurgePlatformAsync(platform);
        Assert.Equal(keep.Id, Assert.Single(await service.ListAsync()).Id);
        await using var check = await host.Factory.CreateDbContextAsync();
        Assert.Empty(await check.ContactKeyAliases.ToListAsync());
        Assert.Empty(await check.AccountSearches.ToListAsync());
        Assert.Single(await check.PostHistory.ToListAsync());
    }

    [Fact]
    public async Task Saving_last_account_removed_deletes_contact_and_new_empty_contacts_are_not_saved()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var service = host.Provider.GetRequiredService<ContactService>();
        var contact = new Contact { Key = "delete", DisplayName = "Delete", XUsername = "only" };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.Add(contact);
            await db.SaveChangesAsync();
            db.AccountSearches.Add(new() { Key = AccountSearch.KeyFor(contact.Id), ContactId = contact.Id });
            await db.SaveChangesAsync();
        }
        contact.XUsername = null;
        Assert.Null(await service.SaveAsync(contact));
        Assert.Null(await service.GetAsync(contact.Id));
        Assert.Null(await service.SaveAsync(new Contact { Key = "new", DisplayName = "New" }));
        Assert.Empty(await service.ListAsync());
        await using var check = await host.Factory.CreateDbContextAsync();
        Assert.Empty(await check.AccountSearches.ToListAsync());
    }

    [Fact]
    public void Manual_merge_limits_selection_and_prunes_only_affected_suggestions_without_scanning()
    {
        var page = new Contacts();
        var a = new Contact { Id = 1, Key = "a", DisplayName = "A" };
        var b = new Contact { Id = 2, Key = "b", DisplayName = "B" };
        var c = new Contact { Id = 3, Key = "c", DisplayName = "C" };
        var d = new Contact { Id = 4, Key = "d", DisplayName = "D" };
        void Select(Contact contact, bool selected) => typeof(Contacts).GetMethod("SetMergeSelection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page, [contact, selected]);
        var selected = (Dictionary<int, Contact>)GetField(page, "mergeSelection")!;
        Select(a, true);
        Select(b, true);
        Select(c, true);
        Assert.Equal([1, 2], selected.Keys);
        Select(a, false);
        Select(c, true);
        Assert.Equal(2, selected.Count);
        Assert.Contains(3, selected.Keys);
        SetField(page, "conflicts", new List<ContactConflict>
        {
            Pair(a, b), Pair(b, c), Pair(c, d)
        });
        typeof(Contacts).GetMethod("RemoveMergedSelections", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page, [1, 2]);
        var remaining = Assert.Single((IReadOnlyList<ContactConflict>)GetField(page, "conflicts")!);
        Assert.Equal(3, remaining.Left.Id);
        Assert.Equal(4, remaining.Right.Id);
        Assert.Empty(selected);
        Assert.False((bool)GetField(page, "scanningConflicts")!);
        typeof(Contacts).GetMethod("ToggleManualMerge", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
        Assert.True((bool)GetField(page, "manualMerge")!);
        Select(a, true);
        typeof(Contacts).GetMethod("ToggleManualMerge", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
        Assert.False((bool)GetField(page, "manualMerge")!);
        Assert.Empty(selected);
    }

    [Fact]
    public async Task Contacts_exposes_manual_merge_selection_without_a_scan()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.Add(new Contact { Key = "first", DisplayName = "First" });
            await db.SaveChangesAsync();
        }
        var html = await host.RenderDialogAsync<Contacts>([], page =>
        {
            typeof(Contacts).GetMethod("ToggleManualMerge", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
            typeof(Microsoft.AspNetCore.Components.ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
            return Task.CompletedTask;
        });
        Assert.Contains("Review selected merge", html);
        Assert.Contains("Select First for merge", html);
        Assert.Contains("0 / 2 selected", html);
        Assert.Contains("No scan is run", html);
        Assert.DoesNotContain("Checking contacts for conflicts", html);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Conflict_scan_reports_pair_progress_and_honors_cancellation(int count)
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            for (var i = 0; i < count; i++)
                db.Contacts.Add(new Contact { Key = $"person{i}", DisplayName = $"Person {i}", XUsername = "shared" });
            await db.SaveChangesAsync();
        }
        var service = host.Provider.GetRequiredService<ContactService>();
        var progress = new ConflictProgress();
        var suggestions = await service.FindConflictsAsync(progress: progress);
        long total = count;
        Assert.Equal((0L, total), progress.Values[0]);
        Assert.Equal((total, total), progress.Values[^1]);
        Assert.Equal((long)count * (count - 1) / 2, suggestions.Count);
        Assert.All(progress.Values, value => Assert.InRange(value.Completed, 0, value.Total));
        Assert.True(progress.Values.Zip(progress.Values.Skip(1)).All(pair => pair.First.Completed <= pair.Second.Completed));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FindConflictsAsync(cancellation.Token, progress));
    }

    private static bool IsPair(ContactConflict s, string x, string y) =>
        s.Left.Key == x && s.Right.Key == y || s.Left.Key == y && s.Right.Key == x;

    private static ContactConflict Pair(Contact a, Contact b) => new(a, b, ContactService.ScoreDuplicate(a, b));

    private sealed class ConflictProgress : IProgress<(long Completed, long Total)>
    {
        public List<(long Completed, long Total)> Values { get; } = [];
        public void Report((long Completed, long Total) value) => Values.Add(value);
    }

    [Theory]
    [InlineData(Platform.X)]
    [InlineData(Platform.Bluesky)]
    [InlineData(Platform.LinkedIn)]
    public async Task Purge_removes_only_target_identity_verification_and_candidates_across_all_contacts(Platform platform)
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var service = host.Provider.GetRequiredService<ContactService>();
        var now = DateTime.UtcNow;
        var contacts = new[]
        {
            new Contact { Key = "first", DisplayName = "User edited", Bio = "Keep", AvatarUrl = "avatar",
                XUsername = "first", XUserId = "1", XVerifiedByCopilotAtUtc = now,
                BlueskyHandle = "first.bsky.social", BlueskyDid = "did:plc:first",
                LinkedInProfileUrl = "https://www.linkedin.com/in/first", LinkedInUrn = "urn:li:person:1",
                LinkedInName = "First", LinkedInVerifiedByCopilotAtUtc = now, UseCount = 9, LastUsedAtUtc = now },
            new Contact { Key = "idonly", DisplayName = "ID only", XUserId = "2", BlueskyDid = "did:plc:second", LinkedInUrn = "urn:li:person:2" },
            new Contact { Key = "none", DisplayName = "None" }
        };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(contacts);
            db.PostHistory.Add(new() { Template = "Hello {@first}", ResultsJson = """[{"history":"keep"}]""" });
            await db.SaveChangesAsync();
            foreach (var c in contacts)
            foreach (var suffix in new[] { "", ":X", ":LinkedIn" })
                db.AccountSearches.Add(new()
                {
                    Key = AccountSearch.KeyFor(c.Id) + suffix, ContactId = c.Id, Status = AccountSearchStatus.Done,
                    CandidatesJson = """[{"Platform":"X","Handle":"first"},{"Platform":1,"Handle":"first.bsky.social"},{"Platform":"LinkedIn","Handle":"first"}]"""
                });
            db.AccountSearches.Add(new() { Key = "malformed", CandidatesJson = "{bad" });
            db.AccountSearches.Add(new() { Key = "running", ContactId = contacts[0].Id, Status = AccountSearchStatus.Running });
            await db.SaveChangesAsync();
        }
        Assert.Equal(2, await service.CountPurgeAsync(platform));
        Assert.Equal(2, await service.PurgePlatformAsync(platform));
        var saved = await service.ListAsync();
        Assert.Equal(2, saved.Count);
        var first = saved.Single(c => c.Key == "first");
        Assert.Equal("User edited", first.DisplayName);
        Assert.Equal("Keep", first.Bio);
        Assert.Equal("avatar", first.AvatarUrl);
        Assert.Equal(9, first.UseCount);
        Assert.Equal(now, first.LastUsedAtUtc);
        Assert.Equal(contacts[0].CreatedAtUtc, first.CreatedAtUtc);
        Assert.Equal(contacts[0].Source, first.Source);
        if (platform != Platform.LinkedIn)
        {
            Assert.Equal("urn:li:person:1", first.LinkedInUrn);
            Assert.Equal("First", first.LinkedInName);
            Assert.Equal(now, first.LinkedInVerifiedByCopilotAtUtc);
        }
        if (platform == Platform.X)
        {
            Assert.All(saved, c => { Assert.Null(c.XUsername); Assert.Null(c.XUserId); Assert.Null(c.XVerifiedByCopilotAtUtc); });
            Assert.Equal("did:plc:first", first.BlueskyDid);
            Assert.Equal("first.bsky.social", first.BlueskyHandle);
        }
        else if (platform == Platform.Bluesky)
        {
            Assert.All(saved, c => { Assert.Null(c.BlueskyHandle); Assert.Null(c.BlueskyDid); });
            Assert.Equal("first", first.XUsername);
            Assert.Equal("1", first.XUserId);
            Assert.Equal(now, first.XVerifiedByCopilotAtUtc);
        }
        else
        {
            Assert.All(saved, c =>
            {
                Assert.Null(c.LinkedInProfileUrl);
                Assert.Null(c.LinkedInUrn);
                Assert.Null(c.LinkedInName);
                Assert.Null(c.LinkedInVerifiedByCopilotAtUtc);
            });
            Assert.Equal("first", first.XUsername);
            Assert.Equal("1", first.XUserId);
            Assert.Equal(now, first.XVerifiedByCopilotAtUtc);
            Assert.Equal("first.bsky.social", first.BlueskyHandle);
            Assert.Equal("did:plc:first", first.BlueskyDid);
        }
        await using var check = await host.Factory.CreateDbContextAsync();
        foreach (var row in await check.AccountSearches.Where(c => c.Status == AccountSearchStatus.Done && c.Key != "malformed").ToListAsync())
        {
            using var json = JsonDocument.Parse(row.CandidatesJson);
            Assert.Equal(2, json.RootElement.GetArrayLength());
            Assert.DoesNotContain(json.RootElement.EnumerateArray(), item =>
                item.GetProperty("Platform").ToString().Equals(platform.ToString(), StringComparison.OrdinalIgnoreCase) ||
                item.GetProperty("Platform").ToString() == ((int)platform).ToString());
        }
        Assert.Equal("[]", (await check.AccountSearches.SingleAsync(c => c.Key == "malformed")).CandidatesJson);
        Assert.Equal(AccountSearchStatus.Failed, (await check.AccountSearches.SingleAsync(c => c.Key == "running")).Status);
        Assert.Equal("Hello {@first}", (await check.PostHistory.SingleAsync()).Template);
        Assert.Contains("history", (await check.PostHistory.SingleAsync()).ResultsJson);
        Assert.Contains("maintenance", await service.SaveAsync(contacts[0], platforms: []));
        var searches = host.Provider.GetRequiredService<AccountSearchService>();
        await searches.SaveAsync(AccountSearch.KeyFor(first.Id), first.Id, 0, row => row.CandidatesJson = "restored-stale-result");
        Assert.DoesNotContain("restored", (await check.AccountSearches.AsNoTracking().FirstAsync()).CandidatesJson);
        Assert.Equal(0, host.Http.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complementary_merge_preserves_selected_survivor_usage_aliases_history_and_scoped_caches(bool keepRight)
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var service = host.Provider.GetRequiredService<ContactService>();
        var left = new Contact { Key = "old", DisplayName = "Left edited", Bio = "left bio", XUsername = "jane",
            XUserId = "1", UseCount = 3, LastUsedAtUtc = DateTime.UtcNow.AddDays(-2) };
        var right = new Contact { Key = "current", DisplayName = "Right edited", Bio = "", AvatarUrl = "right-avatar",
            BlueskyHandle = "jane.bsky.social", BlueskyDid = "did:plc:jane", UseCount = 8, LastUsedAtUtc = DateTime.UtcNow };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(left, right);
            await db.SaveChangesAsync();
            db.ContactKeyAliases.Add(new() { Key = "earlier", ContactId = keepRight ? left.Id : right.Id });
            db.PostHistory.Add(new() { Template = "Hi {@old} and {@current} and {@earlier}", ResultsJson = """[{"Url":"unchanged"}]""" });
            db.AccountSearches.Add(new()
            {
                Key = AccountSearch.KeyFor(keepRight ? left.Id : right.Id) + ":LinkedIn",
                ContactId = keepRight ? left.Id : right.Id, Status = AccountSearchStatus.Done,
                CandidatesJson = """[{"Platform":"LinkedIn","Handle":"jane"}]"""
            });
            await db.SaveChangesAsync();
        }
        var survivor = keepRight ? right : left;
        var source = keepRight ? left : right;
        var preview = await service.PreviewMergeAsync(left.Id, right.Id);
        Assert.Empty(preview.Collisions);
        var merged = await service.MergeAsync(preview, survivor.Id, new Dictionary<Platform, int>());
        Assert.Equal(survivor.Id, merged.Id);
        Assert.Equal(survivor.Key, merged.Key);
        Assert.Equal(survivor.DisplayName, merged.DisplayName);
        Assert.Equal(survivor.Bio, merged.Bio);
        Assert.Equal(survivor.AvatarUrl, merged.AvatarUrl);
        Assert.Equal(survivor.CreatedAtUtc, merged.CreatedAtUtc);
        Assert.Equal(survivor.Source, merged.Source);
        Assert.Equal(11, merged.UseCount);
        Assert.Equal(right.LastUsedAtUtc, merged.LastUsedAtUtc);
        Assert.Equal("jane", merged.XUsername);
        Assert.Equal("1", merged.XUserId);
        Assert.Equal("did:plc:jane", merged.BlueskyDid);
        Assert.Single(await service.ListAsync());
        var lookup = await service.GetByKeysAsync(["OLD", "current", "earlier"]);
        Assert.Equal(3, lookup.Count);
        Assert.All(lookup.Values, c => Assert.Equal(survivor.Id, c.Id));
        // The exact unchanged template covers history and the same text in a persisted browser draft.
        const string template = "Hi {@old} and {@current} and {@earlier}";
        var rendered = PostRenderer.RenderX(template, key => lookup.GetValueOrDefault(key));
        Assert.Equal("Hi @jane and @jane and @jane", rendered.DisplayText);
        Assert.Empty(rendered.Warnings);
        await service.MarkUsedAsync(["old", "current", "earlier"]);
        Assert.Equal(12, (await service.GetAsync(survivor.Id))!.UseCount); // aliases never multiply a single post's usage
        await using var check = await host.Factory.CreateDbContextAsync();
        Assert.Equal(template, (await check.PostHistory.SingleAsync()).Template);
        Assert.Contains("unchanged", (await check.PostHistory.SingleAsync()).ResultsJson);
        var cache = await check.AccountSearches.SingleAsync();
        Assert.Equal(AccountSearch.KeyFor(survivor.Id) + ":LinkedIn", cache.Key);
        Assert.Equal(survivor.Id, cache.ContactId);
        Assert.Contains("jane", cache.CandidatesJson);
        Assert.Contains("reserved", await service.SaveAsync(new() { Key = source.Key, DisplayName = "Steal key" }, platforms: []));
        var created = await service.CreateFromNameAsync(source.Key);
        Assert.NotEqual(source.Key, created.Key);
        await host.Provider.GetRequiredService<AccountSearchService>().SaveAsync(AccountSearch.KeyFor(source.Id),
            source.Id, source.DiscoveryRevision, row => row.CandidatesJson = "stale");
        Assert.Equal(1, await check.AccountSearches.CountAsync());
        Assert.Equal(0, host.Http.RequestCount);
    }

    [Fact]
    public async Task Merge_rejects_unresolved_collisions_invalid_survivor_and_stale_preview_without_writes()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var service = host.Provider.GetRequiredService<ContactService>();
        var left = new Contact { Key = "a", DisplayName = "Jane", XUsername = "one", XUserId = "1",
            XVerifiedByCopilotAtUtc = DateTime.UtcNow, UseCount = 2 };
        var right = new Contact { Key = "b", DisplayName = "Jane", XUsername = "two", XUserId = "2", UseCount = 4 };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(left, right);
            await db.SaveChangesAsync();
            foreach (var c in new[] { left, right })
                db.AccountSearches.Add(new() { Key = AccountSearch.KeyFor(c.Id), ContactId = c.Id,
                    Status = AccountSearchStatus.Done, CandidatesJson = """[{"Platform":"X","Handle":"wrong"}]""" });
            await db.SaveChangesAsync();
        }
        var preview = await service.PreviewMergeAsync(left.Id, right.Id);
        Assert.Equal([Platform.X], preview.Collisions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MergeAsync(preview, left.Id, new Dictionary<Platform, int>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MergeAsync(preview, -1, new Dictionary<Platform, int> { [Platform.X] = right.Id }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MergeAsync(preview, left.Id, new Dictionary<Platform, int> { [Platform.X] = -1 }));
        await using (var check = await host.Factory.CreateDbContextAsync())
        {
            Assert.Equal(2, await check.Contacts.CountAsync());
            Assert.Equal(0, await check.ContactKeyAliases.CountAsync());
            Assert.All(await check.AccountSearches.ToListAsync(), c => Assert.Contains("wrong", c.CandidatesJson));
            await check.Contacts.Where(c => c.Id == right.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Bio, "new edit"));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MergeAsync(preview, left.Id, new Dictionary<Platform, int> { [Platform.X] = right.Id }));
        preview = await service.PreviewMergeAsync(left.Id, right.Id);
        var result = await service.MergeAsync(preview, left.Id, new Dictionary<Platform, int> { [Platform.X] = right.Id });
        Assert.Equal("two", result.XUsername);
        Assert.Equal("2", result.XUserId);
        Assert.Null(result.XVerifiedByCopilotAtUtc); // never carry verification from the discarded identity
        Assert.Equal(6, result.UseCount);
        await using var final = await host.Factory.CreateDbContextAsync();
        var cache = await final.AccountSearches.SingleAsync();
        Assert.Equal("[]", cache.CandidatesJson);
        Assert.Equal(AccountSearchStatus.Failed, cache.Status);
        Assert.Contains("Conflicting", cache.Error);
    }

    [Fact]
    public async Task Suggestions_reuse_normalized_identity_profile_evidence_and_similar_names_without_merging()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(
                new Contact { Key = "a", DisplayName = "Different", XUsername = "https://x.com/Jane/" },
                new Contact { Key = "b", DisplayName = "Other", XUsername = "@jane" },
                new Contact { Key = "c", DisplayName = "Name", BlueskyHandle = "c.bsky.social" },
                new Contact { Key = "d", DisplayName = "Distinct", Bio = "https://bsky.app/profile/c.bsky.social" },
                new Contact { Key = "e", DisplayName = "Jane Doe 🦋" },
                new Contact { Key = "f", DisplayName = "jane-doe" },
                new Contact { Key = "g", DisplayName = "Jame Doe" },
                new Contact { Key = "h", DisplayName = "Unrelated" });
            await db.SaveChangesAsync();
        }
        var service = host.Provider.GetRequiredService<ContactService>();
        var suggestions = await service.FindConflictsAsync();
        Assert.Contains(suggestions, s => IsPair(s, "a", "b") && s.Reason.Contains("Same X handle"));
        Assert.Contains(suggestions, s => IsPair(s, "c", "d") && s.Reason.Contains("links to"));
        Assert.Contains(suggestions, s => IsPair(s, "e", "f") && s.Reason.Contains("Identical normalized names"));
        Assert.Contains(suggestions, s => IsPair(s, "e", "g") && s.Reason.Contains("one character"));
        Assert.True(suggestions.Zip(suggestions.Skip(1)).All(p => p.First.Score >= p.Second.Score));
        Assert.DoesNotContain(suggestions, s => s.Left.Key == "h" || s.Right.Key == "h");
        Assert.Equal(8, (await service.ListAsync()).Count);
        Assert.Equal(0, host.Http.RequestCount);
    }

    [Theory]
    [MemberData(nameof(PlatformSelectionTests.Selections), MemberType = typeof(PlatformSelectionTests))]
    public async Task Contacts_show_purge_only_for_enabled_platforms(Platform[] platforms)
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var html = await host.RenderAsync<Contacts>(platforms);
        // Purge actions live in the closed overflow menu, so check what it would offer.
        var purge = await host.InspectAsync<Contacts, IReadOnlyList<Platform>>(platforms,
            page => PlatformSelectionTests.ContactsPlatforms(page, "PurgePlatforms"));
        Assert.Equal(platforms.OrderBy(p => p), purge.OrderBy(p => p));
        Assert.Contains("Handle conflicts", html);
        Assert.Contains("More contact actions", html);
        Assert.DoesNotContain("Purge X", html); // destructive actions stay out of the toolbar
    }

    [Theory]
    [InlineData(Platform.X)]
    [InlineData(Platform.Bluesky)]
    [InlineData(Platform.LinkedIn)]
    public async Task Purge_dialog_describes_global_impact_and_requires_confirmation(Platform platform)
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var html = WebUtility.HtmlDecode(await host.RenderDialogAsync<PurgeContactsDialog>(
            new() { [nameof(PurgeContactsDialog.TargetPlatform)] = platform }));
        Assert.Contains($"Remove ALL {platform} identities across ALL contacts and accounts", html);
        Assert.Contains("not just the current filter", html);
        Assert.Contains("identity IDs/URNs", html);
        Assert.Contains("verification timestamps", html);
        Assert.Contains("Contacts with no remaining accounts are deleted", html);
        Assert.Contains("I confirm removal", html);
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(html, @"<button[^>]*disabled[^>]*>[^<]*(?:<[^>]+>)*Purge", System.Text.RegularExpressions.RegexOptions.Singleline), html);
    }

    [Fact]
    public async Task Merge_dialog_previews_both_identities_survivor_and_collisions_before_confirmation()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var service = host.Provider.GetRequiredService<ContactService>();
        var left = new Contact { Key = "a", DisplayName = "First", XUsername = "one", XUserId = "1",
            AvatarUrl = "https://example.test/avatar.jpg", BlueskyHandle = "first.bsky.social",
            LinkedInProfileUrl = "https://www.linkedin.com/in/first" };
        var right = new Contact { Key = "b", DisplayName = "Second", XUsername = "two", XUserId = "2" };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(left, right);
            await db.SaveChangesAsync();
        }
        var html = WebUtility.HtmlDecode(await host.RenderDialogAsync<MergeContactsDialog>(
            new() { [nameof(MergeContactsDialog.Preview)] = await service.PreviewMergeAsync(left.Id, right.Id) }));
        Assert.Contains("First", html);
        Assert.Contains("Second", html);
        Assert.Contains("src=\"https://example.test/avatar.jpg\"", html);
        Assert.Contains("avatar-placeholder", html);
        Assert.DoesNotContain("Avatar:", html);
        Assert.Contains("href=\"https://x.com/one\"", html);
        Assert.Contains("href=\"https://x.com/two\"", html);
        Assert.Contains("href=\"https://bsky.app/profile/first.bsky.social\"", html);
        Assert.Contains("href=\"https://www.linkedin.com/in/first\"", html);
        Assert.Contains("Open X profile", html);
        Assert.Contains("Open Bluesky profile", html);
        Assert.Contains("Open LinkedIn profile", html);
        Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("X: one", html);
        Assert.Contains("X: two", html);
        Assert.Contains("Surviving contact", html);
        Assert.Contains("Keep which X identity?", html);
        Assert.Contains("I confirm these are the same contact", html);
        Assert.Contains("aliases", html);
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(html, @"<button[^>]*disabled[^>]*>.*?Confirm merge", System.Text.RegularExpressions.RegexOptions.Singleline), html);
        Assert.Equal(2, (await service.ListAsync()).Count);
    }

    [Fact]
    public async Task Dialog_actions_are_noops_until_confirmed_and_merge_requires_survivor_and_collision_choices()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var service = host.Provider.GetRequiredService<ContactService>();
        var left = new Contact { Key = "a", DisplayName = "First", XUsername = "one", XUserId = "1" };
        var right = new Contact { Key = "b", DisplayName = "Second", XUsername = "two", XUserId = "2" };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(left, right);
            await db.SaveChangesAsync();
        }
        var preview = await service.PreviewMergeAsync(left.Id, right.Id);
        await host.RenderDialogAsync<MergeContactsDialog>(new() { [nameof(MergeContactsDialog.Preview)] = preview }, async dialog =>
        {
            await InvokeActionAsync(dialog, "MergeAsync");
            Assert.Equal(2, (await service.ListAsync()).Count);
            SetField(dialog, "confirmed", true);
            await InvokeActionAsync(dialog, "MergeAsync"); // no survivor
            Assert.Equal(2, (await service.ListAsync()).Count);
            SetField(dialog, "survivorId", right.Id);
            await InvokeActionAsync(dialog, "MergeAsync"); // unresolved X collision
            Assert.Equal(2, (await service.ListAsync()).Count);
            var choices = (Dictionary<Platform, int>)GetField(dialog, "choices")!;
            choices[Platform.X] = left.Id;
            await InvokeActionAsync(dialog, "MergeAsync");
            var saved = Assert.Single(await service.ListAsync());
            Assert.Equal(right.Id, saved.Id);
            Assert.Equal("one", saved.XUsername);
        });
        await host.RenderDialogAsync<PurgeContactsDialog>(new() { [nameof(PurgeContactsDialog.TargetPlatform)] = Platform.X }, async dialog =>
        {
            await InvokeActionAsync(dialog, "PurgeAsync");
            Assert.Equal("one", (await service.GetAsync(right.Id))!.XUsername);
            SetField(dialog, "confirmed", true);
            await InvokeActionAsync(dialog, "PurgeAsync");
            Assert.Null(await service.GetAsync(right.Id));
        });
    }

    [Fact]
    public async Task Review_dialog_not_a_match_records_pair_without_merging_and_is_hidden_in_import_mode()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var service = host.Provider.GetRequiredService<ContactService>();
        var left = new Contact { Key = "a", DisplayName = "First", XUsername = "same" };
        var right = new Contact { Key = "b", DisplayName = "Second", XUsername = "same" };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(left, right);
            await db.SaveChangesAsync();
        }
        var preview = await service.PreviewMergeAsync(left.Id, right.Id);
        var parameters = new Dictionary<string, object?> { [nameof(MergeContactsDialog.Preview)] = preview };
        Assert.Contains("Not a match", await host.RenderDialogAsync<MergeContactsDialog>(parameters));
        Assert.DoesNotContain("Not a match", await host.RenderDialogAsync<MergeContactsDialog>(
            new() { [nameof(MergeContactsDialog.Preview)] = preview, [nameof(MergeContactsDialog.ImportMode)] = true }));

        MudBlazor.DialogResult? result = null;
        await host.RenderDialogAsync<MergeContactsDialog>(parameters,
            dialog => InvokeActionAsync(dialog, "NotMatchAsync"), answer => result = answer);
        Assert.Equal(MergeContactsDialog.NotMatchResult, result?.Data);
        Assert.Equal([ContactService.PairKey(left.Id, right.Id)], await service.ListNotDuplicatesAsync());
        Assert.Equal(2, (await service.ListAsync()).Count);
        Assert.Empty(await service.FindConflictsAsync());
    }

    [Fact]
    public async Task Clicking_identity_card_selects_survivor_and_resets_confirmation()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var service = host.Provider.GetRequiredService<ContactService>();
        var left = new Contact { Key = "a", DisplayName = "First", XUsername = "one" };
        var right = new Contact { Key = "b", DisplayName = "Second", BlueskyHandle = "two.bsky.social" };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(left, right);
            await db.SaveChangesAsync();
        }
        var preview = await service.PreviewMergeAsync(left.Id, right.Id);
        await host.RenderDialogAsync<MergeContactsDialog>(new() { [nameof(MergeContactsDialog.Preview)] = preview }, dialog =>
        {
            var select = dialog.GetType().GetMethod("SelectSurvivor", BindingFlags.NonPublic | BindingFlags.Instance)!;
            SetField(dialog, "confirmed", true);
            select.Invoke(dialog, [right.Id]);
            Assert.Equal(right.Id, GetField(dialog, "survivorId"));
            Assert.Equal(false, GetField(dialog, "confirmed"));
            SetField(dialog, "confirmed", true);
            select.Invoke(dialog, [right.Id]); // re-selecting the current survivor keeps confirmation
            Assert.Equal(true, GetField(dialog, "confirmed"));
            select.Invoke(dialog, [left.Id]);
            Assert.Equal(left.Id, GetField(dialog, "survivorId"));
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(Platform.Bluesky)]
    [InlineData(Platform.LinkedIn)]
    public async Task Other_platform_collisions_require_complete_identity_selection(Platform platform)
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var service = host.Provider.GetRequiredService<ContactService>();
        var left = new Contact { Key = "a", DisplayName = "Left", BlueskyHandle = "same.bsky.social", BlueskyDid = "did:plc:a",
            LinkedInUrn = "urn:li:person:a", LinkedInName = "Left", LinkedInProfileUrl = "https://www.linkedin.com/in/a",
            LinkedInVerifiedByCopilotAtUtc = DateTime.UtcNow };
        var right = new Contact { Key = "b", DisplayName = "Right", BlueskyHandle = "same.bsky.social", BlueskyDid = "did:plc:b",
            LinkedInUrn = "urn:li:person:b", LinkedInName = "Right", LinkedInProfileUrl = "https://www.linkedin.com/in/b" };
        if (platform == Platform.Bluesky)
        {
            right.LinkedInUrn = left.LinkedInUrn;
            right.LinkedInName = left.LinkedInName;
            right.LinkedInProfileUrl = left.LinkedInProfileUrl;
        }
        else right.BlueskyDid = left.BlueskyDid;
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(left, right);
            await db.SaveChangesAsync();
        }
        var preview = await service.PreviewMergeAsync(left.Id, right.Id);
        Assert.Equal([platform], preview.Collisions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MergeAsync(preview, left.Id, new Dictionary<Platform, int>()));
        var merged = await service.MergeAsync(preview, left.Id, new Dictionary<Platform, int> { [platform] = right.Id });
        if (platform == Platform.Bluesky) Assert.Equal("did:plc:b", merged.BlueskyDid);
        else
        {
            Assert.Equal(right.LinkedInUrn, merged.LinkedInUrn);
            Assert.Equal(right.LinkedInName, merged.LinkedInName);
            Assert.Equal(right.LinkedInProfileUrl, merged.LinkedInProfileUrl);
            Assert.Null(merged.LinkedInVerifiedByCopilotAtUtc);
        }
    }

    [Fact]
    public async Task Repeated_merges_redirect_old_aliases_and_deleting_survivor_removes_them()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var service = host.Provider.GetRequiredService<ContactService>();
        var a = await service.CreateFromNameAsync("a");
        var b = await service.CreateFromNameAsync("b");
        var c = await service.CreateFromNameAsync("c");
        await service.MergeAsync(await service.PreviewMergeAsync(a.Id, b.Id), b.Id, new Dictionary<Platform, int>());
        await service.MergeAsync(await service.PreviewMergeAsync(b.Id, c.Id), c.Id, new Dictionary<Platform, int>());
        Assert.All((await service.GetByKeysAsync(["a", "b", "c"])).Values, found => Assert.Equal(c.Id, found.Id));
        Assert.Equal(3, (await service.GetByKeysAsync(["a", "b", "c"])).Count);
        await service.DeleteAsync(c.Id);
        Assert.Empty(await service.GetByKeysAsync(["a", "b", "c"]));
        await using var db = await host.Factory.CreateDbContextAsync();
        Assert.Empty(await db.ContactKeyAliases.ToListAsync());
    }

    [Fact]
    public async Task Failed_merge_rolls_back_even_after_combining_identities_and_canceled_purge_preserves_data()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var service = host.Provider.GetRequiredService<ContactService>();
        var a = new Contact { Key = "a", DisplayName = "A", UseCount = int.MaxValue, XUsername = "a" };
        var b = new Contact { Key = "b", DisplayName = "B", UseCount = 1, BlueskyHandle = "b.bsky.social" };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(a, b);
            await db.SaveChangesAsync();
        }
        var preview = await service.PreviewMergeAsync(a.Id, b.Id);
        await Assert.ThrowsAsync<OverflowException>(() => service.MergeAsync(preview, a.Id, new Dictionary<Platform, int>()));
        Assert.Equal(2, (await service.ListAsync()).Count);
        Assert.Null((await service.GetAsync(a.Id))!.BlueskyHandle);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PurgePlatformAsync(Platform.X, cancellation.Token));
        Assert.Equal("a", (await service.GetAsync(a.Id))!.XUsername);
        await Assert.ThrowsAsync<ArgumentException>(() => service.PurgePlatformAsync((Platform)99));
    }

    [Fact]
    public async Task Schema_upgrade_adds_maintenance_schema_to_existing_database_idempotently()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        await using var db = await host.Factory.CreateDbContextAsync();
        db.Contacts.Add(new() { Key = "legacy", DisplayName = "Keep" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""DROP TABLE "ContactKeyAliases";""");
        await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "Contacts" DROP COLUMN "DiscoveryRevision";""");
        await db.UpgradeSchemaAsync();
        await db.UpgradeSchemaAsync();
        Assert.Equal(0, (await db.Contacts.AsNoTracking().SingleAsync()).DiscoveryRevision);
        Assert.Equal("Keep", (await db.Contacts.AsNoTracking().SingleAsync()).DisplayName);
        Assert.Empty(await db.ContactKeyAliases.ToListAsync());
    }

    private static object? GetField(object component, string field) => component.GetType()
        .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component);
    private static void SetField(object component, string field, object value) => component.GetType()
        .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, value);
    private static Task InvokeActionAsync(object component, string method) => (Task)component.GetType()
        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, null)!;
}
