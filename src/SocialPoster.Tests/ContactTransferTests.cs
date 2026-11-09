using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocialPoster.Core.Data;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

public class ContactTransferTests
{
    [Fact]
    public async Task Export_then_import_into_an_empty_instance_round_trips_contacts_and_aliases()
    {
        await using var source = await PlatformSelectionTests.RenderHost.CreateAsync();
        await using (var db = await source.Factory.CreateDbContextAsync())
        {
            var satya = new Contact
            {
                Key = "satya", DisplayName = "Satya", XUsername = "satyanadella", XUserId = "20571756",
                BlueskyHandle = "satya.bsky.social", BlueskyDid = "did:plc:satya", ThreadsUsername = "satya",
                LinkedInUrn = "urn:li:person:abc", LinkedInName = "Satya Nadella", UseCount = 7,
                Source = ContactSource.XFollows, Bio = "CEO",
            };
            db.Contacts.Add(satya);
            await db.SaveChangesAsync();
            db.ContactKeyAliases.Add(new() { Key = "old-satya", ContactId = satya.Id });
            db.AccountSearches.Add(new() { Key = AccountSearch.KeyFor(satya.Id), ContactId = satya.Id });
            await db.SaveChangesAsync();
        }
        var json = await source.Provider.GetRequiredService<ContactService>().ExportJsonAsync();
        Assert.DoesNotContain("\"id\"", json);
        Assert.DoesNotContain("discoveryRevision", json);

        await using var target = await PlatformSelectionTests.RenderHost.CreateAsync();
        var result = await target.Provider.GetRequiredService<ContactService>().ImportJsonAsync(json);

        Assert.Equal((1, 0, 0, 0), (result.Added, result.Updated, result.Unchanged, result.Skipped));
        await using var check = await target.Factory.CreateDbContextAsync();
        var imported = await check.Contacts.SingleAsync();
        Assert.Equal(("satya", "Satya", "satyanadella", "20571756", "did:plc:satya", "satya", "urn:li:person:abc", 7, ContactSource.XFollows, "CEO"),
            (imported.Key, imported.DisplayName, imported.XUsername, imported.XUserId, imported.BlueskyDid, imported.ThreadsUsername,
                imported.LinkedInUrn, imported.UseCount, imported.Source, imported.Bio));
        var alias = await check.ContactKeyAliases.SingleAsync();
        Assert.Equal(("old-satya", imported.Id), (alias.Key, alias.ContactId));
        Assert.Empty(await check.AccountSearches.ToListAsync());
    }

    [Fact]
    public async Task Import_fills_missing_identities_without_overwriting_and_is_idempotent()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        int existingId;
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var existing = new Contact { Key = "ada", DisplayName = "Ada (edited)", XUsername = "ada", ThreadsUsername = "ada.saved", UseCount = 3 };
            db.Contacts.Add(existing);
            await db.SaveChangesAsync();
            existingId = existing.Id;
        }
        var json = """
            {
              "format": "socialposter.contacts", "version": 1,
              "contacts": [
                { "key": "lovelace", "displayName": "Ada Lovelace", "xUsername": "@ADA", "blueskyHandle": "ada.bsky.social",
                  "threadsUsername": "ada.other", "bio": "Analyst", "useCount": 10 }
              ]
            }
            """;
        var service = host.Provider.GetRequiredService<ContactService>();

        var first = await service.ImportJsonAsync(json);
        var second = await service.ImportJsonAsync(json);

        Assert.Equal((0, 1, 0, 0), (first.Added, first.Updated, first.Unchanged, first.Skipped));
        Assert.Contains(first.Notes, n => n.Contains("Threads"));
        Assert.Equal((0, 0, 1, 0), (second.Added, second.Updated, second.Unchanged, second.Skipped));
        await using var check = await host.Factory.CreateDbContextAsync();
        var saved = await check.Contacts.SingleAsync();
        Assert.Equal((existingId, "ada", "Ada (edited)", "ada", "ada.bsky.social", "ada.saved", "Analyst", 10),
            (saved.Id, saved.Key, saved.DisplayName, saved.XUsername, saved.BlueskyHandle, saved.ThreadsUsername, saved.Bio, saved.UseCount));
        Assert.Equal(1, saved.DiscoveryRevision);
        var alias = await check.ContactKeyAliases.SingleAsync();
        Assert.Equal(("lovelace", existingId), (alias.Key, alias.ContactId));
    }

    [Fact]
    public async Task Import_renames_taken_keys_and_skips_invalid_or_ambiguous_entries()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(
                new Contact { Key = "sam", DisplayName = "Sam One", XUsername = "samone" },
                new Contact { Key = "pat", DisplayName = "Pat", BlueskyHandle = "pat.bsky.social" });
            await db.SaveChangesAsync();
        }
        var json = """
            {
              "format": "socialposter.contacts", "version": 1,
              "contacts": [
                { "key": "sam", "displayName": "Sam Two", "xUsername": "samtwo" },
                { "key": "both", "displayName": "Both", "xUsername": "samone", "blueskyHandle": "pat.bsky.social" },
                { "key": "noaccounts", "displayName": "Nobody" },
                { "key": "badx", "displayName": "Bad X", "xUsername": "this-is-not-valid" },
                { "key": "dup1", "displayName": "Dup", "mastodonAcct": "dup@example.social" },
                { "key": "dup2", "displayName": "Dup again", "mastodonAcct": "@dup@example.social", "linkedInProfileUrl": "https://www.linkedin.com/in/dup" }
              ]
            }
            """;

        var result = await host.Provider.GetRequiredService<ContactService>().ImportJsonAsync(json);

        Assert.Equal((2, 1, 0, 3), (result.Added, result.Updated, result.Unchanged, result.Skipped));
        await using var check = await host.Factory.CreateDbContextAsync();
        var contacts = await check.Contacts.ToListAsync();
        Assert.Equal("sam-2", contacts.Single(c => c.XUsername == "samtwo").Key);
        var dup = contacts.Single(c => c.MastodonAcct == "dup@example.social");
        Assert.Equal(("dup1", "https://www.linkedin.com/in/dup"), (dup.Key, dup.LinkedInProfileUrl));
        Assert.DoesNotContain(contacts, c => c.Key is "both" or "noaccounts" or "badx");
        Assert.Equal(4, contacts.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "format": "something-else", "contacts": [] }""")]
    [InlineData("""{ "format": "socialposter.contacts", "version": 99, "contacts": [] }""")]
    public async Task Import_rejects_files_that_are_not_a_supported_export(string json)
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => host.Provider.GetRequiredService<ContactService>().ImportJsonAsync(json));
    }
}
