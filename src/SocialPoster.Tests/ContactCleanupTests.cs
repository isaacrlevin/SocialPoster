using System.Net;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocialPoster.Components.Shared;
using SocialPoster.Core;
using SocialPoster.Core.Data;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

public class ContactCleanupTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task Delete_all_removes_contacts_aliases_and_searches_but_keeps_history()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var a = new Contact { Key = "a", DisplayName = "A", XUsername = "a" };
            var b = new Contact { Key = "b", DisplayName = "B", BlueskyHandle = "b.bsky.social" };
            db.Contacts.AddRange(a, b);
            await db.SaveChangesAsync();
            db.ContactKeyAliases.Add(new() { Key = "old-a", ContactId = a.Id });
            db.AccountSearches.Add(new() { Key = AccountSearch.KeyFor(a.Id), ContactId = a.Id });
            db.PostHistory.Add(new() { });
            await db.SaveChangesAsync();
        }
        var service = host.Provider.GetRequiredService<ContactService>();
        Assert.Equal(2, await service.CountAllAsync());
        Assert.Equal(2, await service.DeleteAllAsync());
        await using var check = await host.Factory.CreateDbContextAsync();
        Assert.Empty(await check.Contacts.ToListAsync());
        Assert.Empty(await check.ContactKeyAliases.ToListAsync());
        Assert.Empty(await check.AccountSearches.ToListAsync());
        Assert.Single(await check.PostHistory.ToListAsync());
        Assert.Empty(await service.FindConflictsAsync());
    }

    [Fact]
    public async Task Delete_all_dialog_requires_typed_and_checked_confirmation()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.Add(new Contact { Key = "a", DisplayName = "A", XUsername = "a" });
            await db.SaveChangesAsync();
        }
        var html = WebUtility.HtmlDecode(await host.RenderDialogAsync<DeleteAllContactsDialog>([]));
        Assert.Contains("Delete ALL 1 contacts", html);
        Assert.Contains("Type DELETE to confirm", html);
        object? result = null;
        await host.RenderDialogAsync<DeleteAllContactsDialog>([], async dialog =>
        {
            bool CanDelete() => (bool)typeof(DeleteAllContactsDialog).GetProperty("CanDelete", PrivateInstance)!.GetValue(dialog)!;
            typeof(DeleteAllContactsDialog).GetField("confirmed", PrivateInstance)!.SetValue(dialog, true);
            Assert.False(CanDelete());
            typeof(DeleteAllContactsDialog).GetField("typed", PrivateInstance)!.SetValue(dialog, "delete");
            Assert.False(CanDelete());
            typeof(DeleteAllContactsDialog).GetField("typed", PrivateInstance)!.SetValue(dialog, "DELETE");
            Assert.True(CanDelete());
            await (Task)typeof(DeleteAllContactsDialog).GetMethod("DeleteAsync", PrivateInstance)!.Invoke(dialog, null)!;
        }, answer => result = answer.Data);
        Assert.Equal(1, result);
        Assert.Empty(await host.Provider.GetRequiredService<ContactService>().ListAsync());
    }

    [Fact]
    public async Task Purge_can_also_delete_contacts_imported_from_the_platform_that_kept_bio_derived_links()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(
                // Imported from X; LinkedIn came from the X bio. This is the residue that re-triggers duplicate review.
                new Contact { Key = "imported", DisplayName = "Jane Doe", Source = ContactSource.XFollows, XUsername = "jane",
                    XUserId = "1", Bio = "https://www.linkedin.com/in/janedoe", LinkedInProfileUrl = "https://www.linkedin.com/in/janedoe" },
                new Contact { Key = "manual", DisplayName = "Kept", Source = ContactSource.Manual, XUsername = "kept",
                    LinkedInProfileUrl = "https://www.linkedin.com/in/kept" },
                new Contact { Key = "xonly", DisplayName = "Gone", Source = ContactSource.XFollows, XUsername = "gone" });
            await db.SaveChangesAsync();
        }
        var service = host.Provider.GetRequiredService<ContactService>();
        Assert.Equal(1, await service.CountImportedSurvivorsAsync(Platform.X));
        Assert.Equal(0, await service.CountImportedSurvivorsAsync(Platform.LinkedIn));

        Assert.Equal(3, await service.PurgePlatformAsync(Platform.X, deleteImportedContacts: true));
        var remaining = Assert.Single(await service.ListAsync());
        Assert.Equal("manual", remaining.Key);
        Assert.Null(remaining.XUsername);
        Assert.Equal("https://www.linkedin.com/in/kept", remaining.LinkedInProfileUrl);
    }

    [Fact]
    public async Task Plain_purge_keeps_imported_contacts_with_other_accounts()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.Add(new Contact { Key = "imported", DisplayName = "Jane Doe", Source = ContactSource.XFollows,
                XUsername = "jane", LinkedInProfileUrl = "https://www.linkedin.com/in/janedoe" });
            await db.SaveChangesAsync();
        }
        var service = host.Provider.GetRequiredService<ContactService>();
        Assert.Equal(1, await service.PurgePlatformAsync(Platform.X));
        Assert.Equal("Jane Doe", Assert.Single(await service.ListAsync()).DisplayName);
    }

    [Fact]
    public async Task Threads_only_contacts_survive_other_purges_and_threads_can_be_purged()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(
                new Contact { Key = "threads", DisplayName = "Threads only", ThreadsUsername = "zuck" },
                new Contact { Key = "both", DisplayName = "Both", ThreadsUsername = "both", LinkedInProfileUrl = "https://www.linkedin.com/in/both" });
            await db.SaveChangesAsync();
        }
        var service = host.Provider.GetRequiredService<ContactService>();
        await service.PurgePlatformAsync(Platform.X);
        Assert.Equal(2, (await service.ListAsync()).Count);

        Assert.Equal(2, await service.CountPurgeAsync(Platform.Threads));
        Assert.Equal(2, await service.PurgePlatformAsync(Platform.Threads));
        var left = Assert.Single(await service.ListAsync());
        Assert.Equal("both", left.Key);
        Assert.Null(left.ThreadsUsername);
        Assert.Equal("https://www.linkedin.com/in/both", left.LinkedInProfileUrl);
    }

    [Fact]
    public async Task Merge_keeps_the_threads_username_and_flags_threads_collisions()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        int a, b;
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var left = new Contact { Key = "a", DisplayName = "A", XUsername = "a" };
            var right = new Contact { Key = "b", DisplayName = "B", ThreadsUsername = "b_threads" };
            db.Contacts.AddRange(left, right);
            await db.SaveChangesAsync();
            (a, b) = (left.Id, right.Id);
        }
        var service = host.Provider.GetRequiredService<ContactService>();
        var preview = await service.PreviewMergeAsync(a, b);
        Assert.Empty(preview.Collisions);
        var survivor = await service.MergeAsync(preview, a, new Dictionary<Platform, int>());
        Assert.Equal("b_threads", survivor.ThreadsUsername);

        Assert.Contains(Platform.Threads, (await service.PreviewMergeAsync(a, await AddAsync("c", "other"))).Collisions);

        async Task<int> AddAsync(string key, string threads)
        {
            await using var db = await host.Factory.CreateDbContextAsync();
            var c = new Contact { Key = key, DisplayName = key, ThreadsUsername = threads };
            db.Contacts.Add(c);
            await db.SaveChangesAsync();
            return c.Id;
        }
    }
}
