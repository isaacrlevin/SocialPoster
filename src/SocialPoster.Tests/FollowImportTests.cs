using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SocialPoster.Core;
using SocialPoster.Core.Data;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

public class FollowImportTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_imports_check_and_save_the_same_identity_atomically(bool fromX)
    {
        await using var host = await ImportHost.CreateAsync(independentConnections: true);
        async Task<EnsureResult> ImportAsync() => fromX
            ? await host.Contacts.EnsureFromXAsync(new("123", "Jane", "jane", null, ""), ContactSource.XFollows)
            : await host.Contacts.EnsureFromBlueskyAsync(new("did:plc:jane", "jane.bsky.social", "Jane", null, ""), ContactSource.BlueskyFollows);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(ImportAsync)));
        Assert.Single(results, r => r.Created);
        Assert.Single(await host.AllAsync());
        Assert.Single(results.Select(r => r.Contact.Id).Distinct());
    }

    [Theory]
    [InlineData("https://x.com/JANE?lang=en")]
    [InlineData("https://twitter.com/JANE/")]
    [InlineData(" @JANE ")]
    public async Task X_import_recognizes_legacy_profile_links_as_existing_handles(string savedHandle)
    {
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact { Key = "keep", DisplayName = "Edited", XUsername = savedHandle,
            BlueskyHandle = "jane.bsky.social", UseCount = 9 };
        await host.AddAsync(saved);
        var result = await host.Contacts.EnsureFromXAsync(new("123", "Jane", "jane", null, ""), ContactSource.XFollows);
        Assert.False(result.Created);
        Assert.Equal(saved.Id, result.Contact.Id);
        Assert.Equal("keep", result.Contact.Key);
        Assert.Equal(9, result.Contact.UseCount);
        Assert.Equal("jane", result.Contact.XUsername);
        Assert.Single(await host.AllAsync());
        Assert.Empty(host.Http.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Imports_verify_renamed_legacy_handles_before_creating_contacts(bool fromX)
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ConfigureXAsync();
        var saved = new Contact { Key = "keep", DisplayName = "Jane Doe", UseCount = 12,
            XUsername = fromX ? "oldname" : "other_x",
            BlueskyHandle = fromX ? "other.bsky.social" : "old.bsky.social" };
        await host.AddAsync(saved);
        host.Http.Add(fromX
            ? """{"data":{"id":"123","name":"Jane Doe","username":"newname"}}"""
            : """{"did":"did:plc:jane"}""");
        var result = fromX
            ? await host.Contacts.EnsureFromXAsync(new("123", "Jane Doe", "newname", null, ""), ContactSource.XFollows)
            : await host.Contacts.EnsureFromBlueskyAsync(new("did:plc:jane", "new.bsky.social", "Jane Doe", null, ""), ContactSource.BlueskyFollows);
        Assert.False(result.Created);
        Assert.Equal(saved.Id, result.Contact.Id);
        Assert.Equal(12, result.Contact.UseCount);
        Assert.Equal("keep", result.Contact.Key);
        Assert.Single(await host.AllAsync());
        Assert.Single(host.Http.Requests);
    }

    [Fact]
    public async Task Unresolved_old_X_handle_is_flagged_instead_of_creating_a_possible_duplicate()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ConfigureXAsync();
        await host.AddAsync(new() { Key = "keep", DisplayName = "Jane Doe", XUsername = "oldname",
            BlueskyHandle = "jane.bsky.social" });
        host.Http.Add("{}", HttpStatusCode.NotFound);
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            host.Contacts.EnsureFromXAsync(new("123", "Jane Doe", "newname", null, ""), ContactSource.XFollows));
        Assert.Contains("Possible duplicate", error.Message);
        Assert.Single(await host.AllAsync());
    }

    [Fact]
    public async Task Same_name_with_verified_different_X_ID_is_not_merged()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ConfigureXAsync();
        await host.AddAsync(new() { Key = "other", DisplayName = "Jane Doe", XUsername = "otherjane" });
        host.Http.Add("""{"data":{"id":"different","name":"Jane Doe","username":"otherjane"}}""");
        var result = await host.Contacts.EnsureFromXAsync(new("123", "Jane Doe", "newname", null, ""), ContactSource.XFollows);
        Assert.True(result.Created);
        Assert.Equal(2, (await host.AllAsync()).Count);
    }

    [Fact]
    public async Task X_resolves_own_account_signs_and_paginates_including_empty_pages()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ConfigureXAsync();
        host.Http.Add("""{"data":{"id":"self","name":"Self","username":"self"}}""");
        host.Http.Add("""{"data":[{"id":"1","name":"Jane","username":"jane","entities":{"url":{"urls":[{"expanded_url":"https://bsky.app/profile/jane.bsky.social"}]}}}],"meta":{"next_token":"a+b /"}}""");
        host.Http.Add("""{"meta":{"next_token":"last"}}""");
        host.Http.Add("""{"data":[{"id":"2","name":"Bob","username":"bob"}],"meta":{}}""");
        var pages = new List<IReadOnlyList<XUser>>();
        await foreach (var page in host.X.GetFollowsAsync()) pages.Add(page);
        Assert.Equal(2, pages.Count);
        Assert.Contains("bsky.app/profile/jane.bsky.social", pages[0][0].ProfileText);
        Assert.Equal("/2/users/me", host.Http.Requests[0].Path);
        Assert.All(host.Http.Requests, r => Assert.StartsWith("OAuth ", r.Authorization));
        Assert.All(host.Http.Requests.Skip(1), r => Assert.Equal("/2/users/self/following", r.Path));
        Assert.Contains("pagination_token=a%2Bb%20%2F", host.Http.Requests[2].Query);
        Assert.Contains("pagination_token=last", host.Http.Requests[3].Query);
        Assert.Contains("entities", host.Http.Requests[1].Query);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task X_import_gating_makes_no_requests(bool configured, bool intent, bool enabled)
    {
        await using var host = await ImportHost.CreateAsync();
        if (configured) await host.ConfigureXAsync();
        if (intent) await host.Secrets.SetAsync(SettingKeys.XPostMethod, "intent");
        Platform[] platforms = enabled ? [Platform.X] : [Platform.Bluesky];
        Assert.False(await host.X.CanImportFollowsAsync(platforms));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Contacts.SyncXFollowsAsync(platforms: platforms));
        Assert.Empty(host.Http.Requests);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task X_import_surfaces_http_errors_and_preserves_completed_page(int status)
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ConfigureXAsync();
        host.Http.Add("""{"data":{"id":"self","name":"Self","username":"self"}}""");
        host.Http.Add("""{"data":[{"id":"1","name":"Jane","username":"jane"}],"meta":{"next_token":"next"}}""");
        host.Http.Add("""{"detail":"Access unavailable"}""", (HttpStatusCode)status);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => host.Contacts.SyncXFollowsAsync());
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains(status.ToString(), error.Message);
        if (status == 429) Assert.Contains("quota", error.Message);
        Assert.Equal("1", (await host.AllAsync()).Single().XUserId);
    }

    [Fact]
    public async Task X_own_account_error_does_not_request_following()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ConfigureXAsync();
        host.Http.Add("""{"detail":"Invalid token"}""", HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<HttpRequestException>(() => host.Contacts.SyncXFollowsAsync());
        Assert.Single(host.Http.Requests);
        Assert.Empty(await host.AllAsync());
    }

    [Fact]
    public async Task X_repeated_token_and_partial_api_errors_are_not_silent_success()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ConfigureXAsync();
        host.Http.Add("""{"data":{"id":"self","name":"Self","username":"self"}}""");
        host.Http.Add("""{"meta":{"next_token":"same"}}""");
        host.Http.Add("""{"meta":{"next_token":"same"}}""");
        var repeated = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Contacts.SyncXFollowsAsync());
        Assert.Contains("pagination", repeated.Message);
        host.Http.Add("""{"data":{"id":"self","name":"Self","username":"self"}}""");
        host.Http.Add("""{"errors":[{"detail":"quota"}],"data":[{"id":"1","username":"jane","name":"Jane"}]}""");
        var partial = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Contacts.SyncXFollowsAsync());
        Assert.Contains("errors", partial.Message);
        Assert.Empty(await host.AllAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task X_merges_stable_id_or_case_insensitive_username_and_preserves_user_data(bool stableId)
    {
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact
        {
            Key = "custom-key", DisplayName = "My edited name", Bio = "My bio", AvatarUrl = "my-avatar",
            XUserId = stableId ? "123" : null, XUsername = stableId ? "oldname" : "JANE",
            BlueskyDid = "did:plc:jane", BlueskyHandle = "jane.bsky.social",
            LinkedInProfileUrl = "https://www.linkedin.com/in/jane",
            UseCount = 42, LastUsedAtUtc = DateTime.UtcNow.AddDays(-1), Source = ContactSource.Manual,
        };
        await host.AddAsync(saved);
        var result = await host.Contacts.EnsureFromXAsync(new("123", "API name", "jane", "api-avatar", "API bio"), ContactSource.XFollows);
        var repeated = await host.Contacts.EnsureFromXAsync(new("123", "API name", "jane", null, null), ContactSource.XFollows);
        Assert.False(result.Created);
        Assert.False(repeated.Created);
        var actual = Assert.Single(await host.AllAsync());
        Assert.Equal(saved.Id, actual.Id);
        Assert.Equal(saved.Key, actual.Key);
        Assert.Equal(saved.DisplayName, actual.DisplayName);
        Assert.Equal(saved.Bio, actual.Bio);
        Assert.Equal(saved.AvatarUrl, actual.AvatarUrl);
        Assert.Equal(saved.UseCount, actual.UseCount);
        Assert.Equal(saved.LastUsedAtUtc, actual.LastUsedAtUtc);
        Assert.Equal(saved.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(saved.Source, actual.Source);
        Assert.Equal(saved.BlueskyDid, actual.BlueskyDid);
        Assert.Equal(saved.LinkedInProfileUrl, actual.LinkedInProfileUrl);
        Assert.Equal("123", actual.XUserId);
        Assert.Equal("jane", actual.XUsername);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bluesky_merges_stable_id_or_case_insensitive_handle(bool stableId)
    {
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact { Key = "keep", DisplayName = "Edited", BlueskyDid = stableId ? "did:plc:jane" : null,
            BlueskyHandle = stableId ? "old.bsky.social" : "JANE.BSKY.SOCIAL", XUsername = "jane" };
        await host.AddAsync(saved);
        var result = await host.Contacts.EnsureFromBlueskyAsync(new("did:plc:jane", "jane.bsky.social", "Jane", null, ""),
            ContactSource.BlueskyFollows);
        Assert.False(result.Created);
        Assert.Equal(saved.Id, result.Contact.Id);
        Assert.Equal("keep", result.Contact.Key);
        Assert.Equal("Edited", result.Contact.DisplayName);
        Assert.Equal("jane", result.Contact.XUsername);
        Assert.Equal("did:plc:jane", result.Contact.BlueskyDid);
        Assert.Empty(host.Http.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Imports_link_cross_platform_profiles_in_either_direction(bool fromX)
    {
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact { Key = "person", DisplayName = "Edited", UseCount = 7 };
        if (fromX) { saved.BlueskyHandle = "jane.bsky.social"; saved.BlueskyDid = "did:plc:jane"; }
        else { saved.XUsername = "JANE"; saved.XUserId = "123"; }
        await host.AddAsync(saved);
        var result = fromX
            ? await host.Contacts.EnsureFromXAsync(new("123", "Jane", "jane", null, "https://bsky.app/profile/jane.bsky.social"), ContactSource.XFollows)
            : await host.Contacts.EnsureFromBlueskyAsync(new("did:plc:jane", "jane.bsky.social", "Jane", null, "Twitter: @jane"),
                ContactSource.BlueskyFollows);
        Assert.False(result.Created);
        Assert.Equal(saved.Id, result.Contact.Id);
        Assert.Equal("person", result.Contact.Key);
        Assert.Equal(7, result.Contact.UseCount);
        Assert.Equal("123", result.Contact.XUserId);
        Assert.Equal("did:plc:jane", result.Contact.BlueskyDid);
        Assert.Single(await host.AllAsync());
    }

    [Fact]
    public async Task X_import_uses_reverse_bio_evidence_and_linkedin_links()
    {
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact { Key = "jane", DisplayName = "Edited", BlueskyHandle = "jane.bsky.social", Bio = "X: @Jane" };
        await host.AddAsync(saved);
        var result = await host.Contacts.EnsureFromXAsync(new("1", "Jane", "jane", null, ""), ContactSource.XFollows);
        Assert.Equal(saved.Id, result.Contact.Id);
        var linkedIn = new Contact { Key = "bob", DisplayName = "Bob", LinkedInProfileUrl = "https://www.linkedin.com/in/bob/" };
        await host.AddAsync(linkedIn);
        result = await host.Contacts.EnsureFromXAsync(new("2", "Bob", "bob", null, "https://www.linkedin.com/in/bob"), ContactSource.XFollows);
        Assert.Equal(linkedIn.Id, result.Contact.Id);
    }

    [Fact]
    public async Task Display_name_or_similar_cross_platform_handle_is_not_a_match()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.AddAsync(new() { Key = "saved", DisplayName = "Jane", BlueskyHandle = "jane.bsky.social" });
        var result = await host.Contacts.EnsureFromXAsync(new("1", "Jane", "jane", null, ""), ContactSource.XFollows);
        Assert.True(result.Created);
        Assert.Equal(2, (await host.AllAsync()).Count);
    }

    [Fact]
    public async Task Ambiguous_and_conflicting_identities_are_skipped_with_counts_and_progress()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ConfigureXAsync();
        await host.AddAsync(new() { Key = "a", DisplayName = "A", XUsername = "Jane" });
        await host.AddAsync(new() { Key = "b", DisplayName = "B", XUsername = "jane" });
        await host.AddAsync(new() { Key = "c", DisplayName = "C", XUsername = "bob", XUserId = "old-id" });
        host.Http.Add("""{"data":{"id":"self","name":"Self","username":"self"}}""");
        host.Http.Add("""{"data":[{"id":"1","name":"Jane","username":"jane"},{"id":"2","name":"Bob","username":"bob"},{"id":"3","name":"New","username":"new"}]}""");
        var progress = new ImmediateProgress();
        var result = await host.Contacts.SyncXFollowsAsync(progress);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(1, result.Added);
        Assert.Equal([1, 2, 3], progress.Counts);
        Assert.Contains("ambiguous", result.ToString());
        Assert.Equal("old-id", (await host.AllAsync()).Single(c => c.Key == "c").XUserId);
    }

    [Fact]
    public async Task Multiple_cross_platform_matches_do_not_overwrite_or_create_duplicate()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.AddAsync(new() { Key = "a", DisplayName = "A", BlueskyHandle = "jane.bsky.social" });
        await host.AddAsync(new() { Key = "b", DisplayName = "B", LinkedInProfileUrl = "https://www.linkedin.com/in/jane" });
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => host.Contacts.EnsureFromXAsync(
            new("1", "Jane", "jane", null, "https://bsky.app/profile/jane.bsky.social https://www.linkedin.com/in/jane"), ContactSource.XFollows));
        Assert.All(await host.AllAsync(), c => Assert.Null(c.XUsername));
    }

    [Fact]
    public async Task Conflicting_or_multiple_hints_do_not_replace_existing_identities()
    {
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact { Key = "jane", DisplayName = "Jane", BlueskyDid = "did:plc:jane",
            BlueskyHandle = "jane.bsky.social", XUsername = "keep", XUserId = "keep-id" };
        await host.AddAsync(saved);
        var result = await host.Contacts.EnsureFromBlueskyAsync(new("did:plc:jane", "jane.bsky.social", "Jane", null, "X: @other"),
            ContactSource.BlueskyFollows);
        Assert.True(result.NeedsReview);
        Assert.Equal("keep", result.Contact.XUsername);
        Assert.Equal("keep-id", result.Contact.XUserId);
        result = await host.Contacts.EnsureFromXAsync(new("2", "Bob", "bob", null, "X: @one Twitter: @two"), ContactSource.XFollows);
        Assert.True(result.NeedsReview);
        Assert.Contains("Multiple", result.Notes.Single());
    }

    [Fact]
    public async Task Repeat_follow_import_reuses_contact_and_respects_disabled_hints()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ConfigureXAsync();
        for (var i = 0; i < 2; i++)
        {
            host.Http.Add("""{"data":{"id":"self","name":"Self","username":"self"}}""");
            host.Http.Add("""{"data":[{"id":"1","name":"Jane","username":"jane","description":"https://bsky.app/profile/jane.bsky.social"}]}""");
        }
        var first = await host.Contacts.SyncXFollowsAsync(platforms: [Platform.X]);
        var second = await host.Contacts.SyncXFollowsAsync(platforms: [Platform.X]);
        Assert.Equal(1, first.Added);
        Assert.Equal(1, second.Updated);
        Assert.Equal(0, second.Added);
        Assert.Null(Assert.Single(await host.AllAsync()).BlueskyHandle);
    }

    [Theory]
    [InlineData(SettingKeys.XConsumerKey)]
    [InlineData(SettingKeys.XConsumerSecret)]
    [InlineData(SettingKeys.XAccessToken)]
    [InlineData(SettingKeys.XAccessTokenSecret)]
    public async Task Missing_any_X_credential_blocks_import(string missing)
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ConfigureXAsync();
        await host.Secrets.SetAsync(missing, null);
        Assert.False(await host.X.CanImportFollowsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Contacts.SyncXFollowsAsync());
        Assert.Empty(host.Http.Requests);
    }

    [Fact]
    public async Task Bluesky_follow_import_merges_cross_platform_and_repeats_without_duplicates()
    {
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact { Key = "custom", DisplayName = "Edited", XUsername = "Jane", UseCount = 8 };
        await host.AddAsync(saved);
        for (var i = 0; i < 2; i++)
        {
            host.Http.Add("""{"follows":[{"did":"did:plc:jane","handle":"jane.bsky.social","displayName":"Jane","description":"X: @jane"}],"cursor":"next"}""");
            host.Http.Add("""{"follows":[{"did":"did:plc:bob","handle":"bob.bsky.social","displayName":"Bob","description":""}]}""");
        }
        var first = await host.Contacts.SyncBlueskyFollowsAsync("self.bsky.social");
        var second = await host.Contacts.SyncBlueskyFollowsAsync("self.bsky.social");
        Assert.Equal(1, first.Added);
        Assert.Equal(1, first.Updated);
        Assert.Equal(0, second.Added);
        Assert.Equal(2, second.Updated);
        Assert.Contains("cursor=next", host.Http.Requests[1].Query);
        Assert.Equal("did:plc:jane", (await host.AllAsync()).Single(c => c.Key == "custom").BlueskyDid);
    }

    [Fact]
    public async Task Stable_ID_wins_over_cross_platform_hint_and_keeps_history_and_search_references()
    {
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact { Key = "custom", DisplayName = "Edited", XUserId = "1", XUsername = "old" };
        await host.AddAsync(saved);
        await host.AddAsync(new() { Key = "other", DisplayName = "Other", BlueskyHandle = "other.bsky.social" });
        await using (var db = host.CreateDbContext())
        {
            db.PostHistory.Add(new() { Template = "Hello {@custom}", ResultsJson = "[]" });
            db.AccountSearches.Add(new() { Key = AccountSearch.KeyFor(saved.Id), ContactId = saved.Id });
            await db.SaveChangesAsync();
        }
        var result = await host.Contacts.EnsureFromXAsync(new("1", "Jane", "jane", null, "https://bsky.app/profile/other.bsky.social"),
            ContactSource.XFollows);
        Assert.Equal(saved.Id, result.Contact.Id);
        Assert.Equal("jane", result.Contact.XUsername);
        Assert.Null(result.Contact.BlueskyHandle);
        Assert.True(result.NeedsReview);
        await using var check = host.CreateDbContext();
        Assert.Equal("Hello {@custom}", (await check.PostHistory.SingleAsync()).Template);
        Assert.Equal(saved.Id, (await check.AccountSearches.SingleAsync()).ContactId);
    }

    [Fact]
    public async Task Stable_ID_rename_cannot_steal_another_contacts_handle()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.AddAsync(new() { Key = "a", DisplayName = "A", XUserId = "1", XUsername = "old" });
        await host.AddAsync(new() { Key = "b", DisplayName = "B", XUserId = "2", XUsername = "new" });
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => host.Contacts.EnsureFromXAsync(
            new("1", "A", "new", null, ""), ContactSource.XFollows));
        Assert.Equal("old", (await host.AllAsync()).Single(c => c.Key == "a").XUsername);
    }

    [Fact]
    public async Task Duplicate_links_are_not_ambiguous_and_lookalike_domains_are_not_evidence()
    {
        var hints = ProfileLinkExtractor.Extract("https://www.linkedin.com/in/jane https://www.linkedin.com/in/jane");
        Assert.False(hints.Ambiguous);
        Assert.Null(ProfileLinkExtractor.Extract("https://evilx.com/jane").XUsername);
        Assert.Null(ProfileLinkExtractor.Extract("https://evillinkedin.com/in/jane").LinkedInProfileUrl);
        Assert.Null(ProfileLinkExtractor.Extract("https://evilbsky.app/profile/jane.bsky.social").BlueskyIdentity);
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact { Key = "keep", DisplayName = "Edited", LinkedInProfileUrl = "https://linkedin.com/in/jane/?tracking=1" };
        await host.AddAsync(saved);
        var result = await host.Contacts.EnsureFromXAsync(new("1", "Jane", "jane", null, "https://www.linkedin.com/in/jane"), ContactSource.XFollows);
        Assert.Equal(saved.Id, result.Contact.Id);
        Assert.Equal(saved.LinkedInProfileUrl, result.Contact.LinkedInProfileUrl);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data":[{"name":"Missing ID","username":"jane"}]}""")]
    public async Task Malformed_X_follow_response_is_not_treated_as_success(string body)
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ConfigureXAsync();
        host.Http.Add("""{"data":{"id":"self","name":"Self","username":"self"}}""");
        host.Http.Add(body);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Contacts.SyncXFollowsAsync());
        Assert.Empty(await host.AllAsync());
    }

    [Fact]
    public async Task Cancellation_stops_before_an_API_request_or_contact_write()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ConfigureXAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Contacts.SyncXFollowsAsync(ct: cancellation.Token));
        Assert.Empty(host.Http.Requests);
        Assert.Empty(await host.AllAsync());
    }

    private sealed class ImmediateProgress : IProgress<int>
    {
        public List<int> Counts { get; } = [];
        public void Report(int value) => Counts.Add(value);
    }

    [Theory]
    [InlineData(Platform.X)]
    [InlineData(Platform.Bluesky)]
    [InlineData(Platform.Mastodon)]
    public async Task Review_sync_routes_pause_unsaved_merge_and_retry_without_repeated_review(Platform platform)
    {
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact { Key = "edited", DisplayName = "Jane Doe", Bio = "Edited bio",
            AvatarUrl = "edited-avatar", UseCount = 7, LastUsedAtUtc = DateTime.UtcNow.AddDays(-1) };
        await host.AddAsync(saved);
        await using (var db = host.CreateDbContext())
        {
            db.ContactKeyAliases.Add(new() { Key = "earlier", ContactId = saved.Id });
            db.PostHistory.Add(new() { Template = "Hi {@earlier}", ResultsJson = "[]" });
            db.AccountSearches.Add(new() { Key = AccountSearch.KeyFor(saved.Id), ContactId = saved.Id,
                Status = AccountSearchStatus.Running, CandidatesJson = """[{"Platform":"X","Handle":"old"}]""" });
            await db.SaveChangesAsync();
        }
        var pending = new TaskCompletionSource<ContactImportDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<ContactImportReview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new ImmediateProgress();
        var sync = await StartReviewSyncAsync(host, platform, (review, ct) =>
        {
            entered.SetResult(review);
            return pending.Task;
        }, progress);
        var review = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(sync.IsCompleted);
        Assert.Empty(progress.Counts);
        Assert.Equal(-1, review.Incoming.Id);
        Assert.Single(review.Candidates);
        Assert.Null(PrimaryForTest(Assert.Single(await host.AllAsync()), platform));
        pending.SetResult(new(ImportReviewAction.Merge, saved.Id, Confirmed: true));
        var result = await sync;
        Assert.Equal(1, result.Updated);
        Assert.Equal([1], progress.Counts);
        var actual = Assert.Single(await host.AllAsync());
        Assert.Equal(saved.Key, actual.Key);
        Assert.Equal(saved.DisplayName, actual.DisplayName);
        Assert.Equal(saved.Bio, actual.Bio);
        Assert.Equal(saved.AvatarUrl, actual.AvatarUrl);
        Assert.Equal(saved.UseCount, actual.UseCount);
        Assert.Equal(saved.LastUsedAtUtc, actual.LastUsedAtUtc);
        Assert.Equal(saved.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(saved.Source, actual.Source);
        Assert.Equal(saved.DiscoveryRevision + 1, actual.DiscoveryRevision);
        Assert.NotNull(PrimaryForTest(actual, platform));
        Assert.Equal(saved.Id, (await host.Contacts.GetByKeysAsync(["earlier"]))["earlier"].Id);
        await using (var db = host.CreateDbContext())
        {
            Assert.Equal("Hi {@earlier}", (await db.PostHistory.SingleAsync()).Template);
            var cache = await db.AccountSearches.SingleAsync();
            Assert.Equal(AccountSearchStatus.Failed, cache.Status);
            Assert.Equal("[]", cache.CandidatesJson);
        }
        var retry = await StartReviewSyncAsync(host, platform, (_, _) => throw new Exception("Retry must not ask again"));
        Assert.Equal(1, (await retry).Updated);
        Assert.Single(await host.AllAsync());
    }

    [Theory]
    [InlineData(Platform.X, ImportReviewAction.KeepSeparate)]
    [InlineData(Platform.Bluesky, ImportReviewAction.KeepSeparate)]
    [InlineData(Platform.Mastodon, ImportReviewAction.KeepSeparate)]
    [InlineData(Platform.X, ImportReviewAction.Skip)]
    [InlineData(Platform.Bluesky, ImportReviewAction.Skip)]
    [InlineData(Platform.Mastodon, ImportReviewAction.Skip)]
    [InlineData(Platform.X, ImportReviewAction.Cancel)]
    [InlineData(Platform.Bluesky, ImportReviewAction.Cancel)]
    [InlineData(Platform.Mastodon, ImportReviewAction.Cancel)]
    public async Task Review_sync_routes_support_explicit_separate_skip_and_cancel(Platform platform, ImportReviewAction action)
    {
        await using var host = await ImportHost.CreateAsync();
        await host.AddAsync(new() { Key = "similar", DisplayName = "Jane-Doe 🦋" });
        var sync = await StartReviewSyncAsync(host, platform, (review, _) =>
        {
            Assert.Single(review.Candidates);
            return Task.FromResult(new ContactImportDecision(action));
        });
        if (action == ImportReviewAction.Cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync);
        else
        {
            var result = await sync;
            Assert.Equal(action == ImportReviewAction.KeepSeparate ? 1 : 0, result.Added);
            Assert.Equal(action == ImportReviewAction.Skip ? 1 : 0, result.Skipped);
        }
        Assert.Equal(action == ImportReviewAction.KeepSeparate ? 2 : 1, (await host.AllAsync()).Count);
    }

    [Theory]
    [InlineData(Platform.X)]
    [InlineData(Platform.Bluesky)]
    [InlineData(Platform.Mastodon)]
    public async Task Sync_without_review_keeps_names_separate(Platform platform)
    {
        await using var host = await ImportHost.CreateAsync();
        await host.AddAsync(new() { Key = "similar", DisplayName = "Jane Doe" });
        var sync = await StartReviewSyncAsync(host, platform, null);
        Assert.Equal(1, (await sync).Added);
        Assert.Equal(2, (await host.AllAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_collision_selects_whole_identity_and_keeps_metadata(bool keepIncoming)
    {
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact { Key = "saved", DisplayName = "Jane Doe", XUserId = "old-id",
            XUsername = "old", XVerifiedByCopilotAtUtc = DateTime.UtcNow, UseCount = 8 };
        await host.AddAsync(saved);
        var result = await host.Contacts.EnsureFromXAsync(new("new-id", "Jane Doe", "new", null, ""),
            ContactSource.XFollows, reviewer: (review, _) =>
            {
                Assert.Equal([Platform.X], review.Candidates.Single().Collisions);
                return Task.FromResult(new ContactImportDecision(ImportReviewAction.Merge, saved.Id,
                    new Dictionary<Platform, int> { [Platform.X] = keepIncoming ? -1 : saved.Id }, true));
            });
        Assert.Equal(keepIncoming ? "new-id" : "old-id", result.Contact.XUserId);
        Assert.Equal(keepIncoming ? "new" : "old", result.Contact.XUsername);
        Assert.Equal(keepIncoming ? null : saved.XVerifiedByCopilotAtUtc, result.Contact.XVerifiedByCopilotAtUtc);
        Assert.Equal(8, result.Contact.UseCount);
        Assert.Equal("saved", result.Contact.Key);
        Assert.Single(await host.AllAsync());
    }

    [Theory]
    [InlineData(Platform.Bluesky, true)]
    [InlineData(Platform.Bluesky, false)]
    [InlineData(Platform.Mastodon, true)]
    [InlineData(Platform.Mastodon, false)]
    public async Task Other_sync_routes_resolve_whole_platform_collisions(Platform platform, bool keepIncoming)
    {
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact { Key = "saved", DisplayName = "Jane Doe",
            BlueskyDid = platform == Platform.Bluesky ? "did:plc:old" : null,
            BlueskyHandle = platform == Platform.Bluesky ? "old.bsky.social" : null,
            MastodonUri = platform == Platform.Mastodon ? "https://old.example/users/old" : null,
            MastodonAcct = platform == Platform.Mastodon ? "old@old.example" : null,
            MastodonProfileUrl = platform == Platform.Mastodon ? "https://old.example/@old" : null };
        await host.AddAsync(saved);
        Contact? incoming = null;
        var sync = await StartReviewSyncAsync(host, platform, (review, _) =>
        {
            incoming = review.Incoming;
            Assert.Equal([platform], review.Candidates.Single().Collisions);
            return Task.FromResult(new ContactImportDecision(ImportReviewAction.Merge, saved.Id,
                new Dictionary<Platform, int> { [platform] = keepIncoming ? -1 : saved.Id }, true));
        });
        Assert.Equal(1, (await sync).Updated);
        var actual = Assert.Single(await host.AllAsync());
        var chosen = keepIncoming ? incoming! : saved;
        Assert.Equal(chosen.BlueskyDid, actual.BlueskyDid);
        Assert.Equal(chosen.BlueskyHandle, actual.BlueskyHandle);
        Assert.Equal(chosen.MastodonUri, actual.MastodonUri);
        Assert.Equal(chosen.MastodonAcct, actual.MastodonAcct);
        Assert.Equal(chosen.MastodonProfileUrl, actual.MastodonProfileUrl);
        Assert.Equal(saved.Key, actual.Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_requires_confirmation_and_every_collision_choice(bool confirmed)
    {
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact { Key = "saved", DisplayName = "Jane Doe", XUserId = "old-id", XUsername = "old" };
        await host.AddAsync(saved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Contacts.EnsureFromXAsync(
            new("new-id", "Jane Doe", "new", null, ""), ContactSource.XFollows,
            reviewer: (_, _) => Task.FromResult(new ContactImportDecision(ImportReviewAction.Merge, saved.Id, Confirmed: confirmed))));
        Assert.Equal("old-id", Assert.Single(await host.AllAsync()).XUserId);
    }

    [Fact]
    public async Task Ambiguous_bio_lists_all_explicit_candidates_without_choosing_or_applying_a_link()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.AddAsync(new() { Key = "one", DisplayName = "First", XUsername = "one" });
        await host.AddAsync(new() { Key = "two", DisplayName = "Second", XUsername = "two" });
        var result = await host.Contacts.EnsureFromBlueskyAsync(new("did:plc:new", "new.bsky.social", "Distinct", null,
            "https://x.com/one https://x.com/two"), ContactSource.BlueskyFollows, reviewer: (review, _) =>
        {
            Assert.Equal(2, review.Candidates.Count);
            Assert.Contains("Ambiguous", review.Reason);
            Assert.Null(review.Incoming.XUsername);
            return Task.FromResult(new ContactImportDecision(ImportReviewAction.KeepSeparate));
        });
        Assert.True(result.Created);
        Assert.True(result.NeedsReview);
        Assert.Null(result.Contact.XUsername);
        Assert.Equal(3, (await host.AllAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_cannot_keep_separate_or_steal_an_identity_from_a_third_contact(bool merge)
    {
        await using var host = await ImportHost.CreateAsync();
        var idOwner = new Contact { Key = "id-owner", DisplayName = "One", XUserId = "id", XUsername = "old" };
        var handleOwner = new Contact { Key = "handle-owner", DisplayName = "Two", XUserId = "other", XUsername = "new" };
        await host.AddAsync(idOwner);
        await host.AddAsync(handleOwner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Contacts.EnsureFromXAsync(
            new("id", "Incoming", "new", null, ""), ContactSource.XFollows, reviewer: (review, _) =>
            {
                Assert.Equal(2, review.Candidates.Count);
                return Task.FromResult(merge
                    ? new ContactImportDecision(ImportReviewAction.Merge, idOwner.Id,
                        new Dictionary<Platform, int> { [Platform.X] = -1 }, true)
                    : new ContactImportDecision(ImportReviewAction.KeepSeparate));
            }));
        Assert.Equal("old", (await host.AllAsync()).Single(c => c.Id == idOwner.Id).XUsername);
        Assert.Equal(2, (await host.AllAsync()).Count);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("usage")]
    [InlineData("purge")]
    [InlineData("new-owner")]
    public async Task Review_rejects_concurrent_changes_and_has_no_write_lock_during_callback(string change)
    {
        await using var host = await ImportHost.CreateAsync(independentConnections: true);
        var saved = new Contact { Key = "saved", DisplayName = "Jane Doe", XUsername = "old", XUserId = "old-id" };
        await host.AddAsync(saved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Contacts.EnsureFromXAsync(
            new("new-id", "Jane Doe", "new", null, ""), ContactSource.XFollows, reviewer: async (_, _) =>
            {
                if (change == "purge") await host.Contacts.PurgePlatformAsync(Platform.X);
                else if (change == "usage") await host.Contacts.MarkUsedAsync(["saved"]);
                else if (change == "new-owner") await host.AddAsync(new() { Key = "owner", DisplayName = "Other", XUsername = "new", XUserId = "new-id" });
                else
                {
                    await using var db = host.CreateDbContext();
                    await db.Contacts.Where(c => c.Id == saved.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Bio, "concurrent edit"));
                }
                return new(ImportReviewAction.Merge, saved.Id, new Dictionary<Platform, int> { [Platform.X] = -1 }, true);
            }));
        Assert.DoesNotContain(await host.AllAsync(), c => c.Id == saved.Id && c.XUserId == "new-id");
    }

    [Fact]
    public async Task Cancellation_releases_a_reviewer_that_does_not_complete_and_leaves_account_unsaved()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.AddAsync(new() { Key = "saved", DisplayName = "Jane Doe" });
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<ContactImportDecision>();
        var sync = await StartReviewSyncAsync(host, Platform.Bluesky, (_, _) =>
        {
            entered.SetResult();
            return never.Task;
        }, ct: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(Assert.Single(await host.AllAsync()).BlueskyDid);
    }

    private static string? PrimaryForTest(Contact c, Platform p) => p switch
    {
        Platform.X => c.XUserId, Platform.Bluesky => c.BlueskyDid, _ => c.MastodonUri
    };

    [Fact]
    public async Task Cancel_after_a_completed_account_preserves_progress_and_retry_does_not_duplicate_it()
    {
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact { Key = "saved", DisplayName = "Jane Doe" };
        await host.AddAsync(saved);
        const string page = """{"follows":[{"did":"did:plc:bob","handle":"bob.bsky.social","displayName":"Bob","description":""},{"did":"did:plc:jane","handle":"jane.bsky.social","displayName":"Jane Doe","description":""}]}""";
        host.Http.Add(page);
        var progress = new ImmediateProgress();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Contacts.SyncBlueskyFollowsAsync(
            "self.bsky.social", progress, reviewer: (_, _) => Task.FromResult(new ContactImportDecision(ImportReviewAction.Cancel))));
        Assert.Equal([1], progress.Counts);
        Assert.Equal(2, (await host.AllAsync()).Count);
        Assert.Null((await host.AllAsync()).Single(c => c.Id == saved.Id).BlueskyDid);
        host.Http.Add(page);
        var retry = await host.Contacts.SyncBlueskyFollowsAsync("self.bsky.social", reviewer: (_, _) =>
            Task.FromResult(new ContactImportDecision(ImportReviewAction.Merge, saved.Id, Confirmed: true)));
        Assert.Equal(0, retry.Added);
        Assert.Equal(2, retry.Updated);
        Assert.Equal(2, (await host.AllAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changing_a_transient_or_saved_preview_is_rejected(bool changeIncoming)
    {
        await using var host = await ImportHost.CreateAsync();
        var saved = new Contact { Key = "saved", DisplayName = "Jane Doe" };
        await host.AddAsync(saved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Contacts.EnsureFromXAsync(
            new("id", "Jane Doe", "jane", null, ""), ContactSource.XFollows, reviewer: (review, _) =>
            {
                if (changeIncoming) review.Incoming.XUserId = "tampered";
                else review.Candidates.Single().Left.DisplayName = "tampered";
                return Task.FromResult(new ContactImportDecision(ImportReviewAction.Merge, saved.Id, Confirmed: true));
            }));
        Assert.Null(Assert.Single(await host.AllAsync()).XUserId);
    }

    // The outer task performs configuration only, exposing the in-flight sync for pause assertions.
    private static async Task<Task<FollowImportResult>> StartReviewSyncAsync(ImportHost host, Platform platform,
        ContactImportReviewer? reviewer, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (platform == Platform.X)
        {
            await host.ConfigureXAsync();
            host.Http.Add("""{"data":{"id":"self","name":"Self","username":"self"}}""");
            host.Http.Add("""{"data":[{"id":"123","name":"Jane Doe","username":"jane"}],"meta":{}}""");
            return host.Contacts.SyncXFollowsAsync(progress, ct, reviewer: reviewer);
        }
        if (platform == Platform.Mastodon)
        {
            await host.Secrets.SetAsync(SettingKeys.MastodonInstance, "https://social.example/");
            await host.Secrets.SetAsync(SettingKeys.MastodonAccessToken, "test-only-token");
            const string account = """{"id":"42","acct":"jane","uri":"https://social.example/users/jane","url":"https://social.example/@jane","display_name":"Jane Doe","note":""}""";
            host.Http.Add(account);
            host.Http.Add("[" + account + "]");
            return host.Contacts.SyncMastodonFollowsAsync(progress, ct, reviewer: reviewer);
        }
        host.Http.Add("""{"follows":[{"did":"did:plc:jane","handle":"jane.bsky.social","displayName":"Jane Doe","description":""}]}""");
        return host.Contacts.SyncBlueskyFollowsAsync("self.bsky.social", progress, ct, reviewer: reviewer);
    }

    private sealed class FakeHttp : HttpMessageHandler
    {
        private readonly Queue<(string Body, HttpStatusCode Status)> responses = [];
        public List<(string Path, string Query, string Authorization)> Requests { get; } = [];
        public void Add(string body, HttpStatusCode status = HttpStatusCode.OK) => responses.Enqueue((body, status));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add((request.RequestUri!.AbsolutePath, request.RequestUri.Query, request.Headers.Authorization?.ToString() ?? ""));
            var response = responses.Dequeue(); // Never makes live network calls.
            return Task.FromResult(new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ImportHost(SqliteConnection connection, DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>, IAsyncDisposable
    {
        private string? temporaryDatabase;
        public FakeHttp Http { get; } = new();
        private HttpClient client = null!;
        public SecretStore Secrets { get; private set; } = null!;
        public XClient X { get; private set; } = null!;
        public ContactService Contacts { get; private set; } = null!;
        public static async Task<ImportHost> CreateAsync(bool independentConnections = false)
        {
            var path = independentConnections ? Path.Combine(Path.GetTempPath(), $"socialposter-import-tests-{Guid.NewGuid():N}.db") : null;
            var connectionString = path is null ? "Data Source=:memory:" : $"Data Source={path};Pooling=False";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var builder = new DbContextOptionsBuilder<AppDbContext>();
            if (independentConnections) builder.UseSqlite(connectionString); else builder.UseSqlite(connection);
            var host = new ImportHost(connection, builder.Options) { temporaryDatabase = path };
            await using var db = host.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            host.Secrets = new(host, new EphemeralDataProtectionProvider());
            host.client = new(host.Http);
            host.X = new(host.client, host.Secrets);
            host.Contacts = new(host, new(host.client, host.Secrets), host.X, NullLogger<ContactService>.Instance,
                new MastodonClient(host.client, host.Secrets));
            return host;
        }
        public AppDbContext CreateDbContext() => new(options);
        public async Task ConfigureXAsync()
        {
            foreach (var key in new[] { SettingKeys.XConsumerKey, SettingKeys.XConsumerSecret, SettingKeys.XAccessToken, SettingKeys.XAccessTokenSecret })
                await Secrets.SetAsync(key, "fake-test-value");
        }
        public async Task AddAsync(Contact contact)
        {
            await using var db = CreateDbContext();
            db.Contacts.Add(contact);
            await db.SaveChangesAsync();
        }
        public async Task<List<Contact>> AllAsync()
        {
            await using var db = CreateDbContext();
            return await db.Contacts.AsNoTracking().ToListAsync();
        }
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await connection.DisposeAsync();
            if (temporaryDatabase is not null) File.Delete(temporaryDatabase);
        }
    }
}
