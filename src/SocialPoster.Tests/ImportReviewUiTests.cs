using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SocialPoster.Components.Pages;
using SocialPoster.Components.Shared;
using SocialPoster.Core;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

public class ImportReviewUiTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task Prompt_explains_pre_save_review_and_offers_yes_no_cancel()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var html = WebUtility.HtmlDecode(await host.RenderDialogAsync<HandleDuplicatesDialog>([]));
        Assert.Contains("Yes, review", html);
        Assert.Contains("Sync without handling", html);
        Assert.Contains("Cancel", html);
        Assert.Contains("before", html);
        Assert.Contains("nothing is merged automatically", html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Prompt_returns_explicit_yes_no_or_cancel_without_import_requests(bool? review)
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        DialogResult? result = null;
        await host.RenderDialogAsync<HandleDuplicatesDialog>([], dialog =>
        {
            typeof(HandleDuplicatesDialog).GetMethod("Choose", PrivateInstance)!.Invoke(dialog, [review]);
            return Task.CompletedTask;
        }, answer => result = answer);
        Assert.NotNull(result);
        Assert.Equal(review is null, result.Canceled);
        if (review is not null) Assert.Equal(review.Value, result.Data);
        Assert.Equal(0, host.Http.RequestCount);
    }

    [Fact]
    public async Task Account_review_lists_every_candidate_and_explicit_alternatives()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var incoming = new Contact { Id = -1, Key = "(incoming — not saved)", DisplayName = "Incoming" };
        var candidates = new[] { 1, 2 }.Select(id => new ContactMergePreview(
            new() { Id = id, Key = $"saved{id}", DisplayName = $"Saved {id}" }, incoming, [], "", "")).ToList();
        var html = WebUtility.HtmlDecode(await host.RenderDialogAsync<ImportDuplicatesDialog>(
            new() { [nameof(ImportDuplicatesDialog.Review)] = new ContactImportReview(incoming, candidates, "Names are not proof") }));
        Assert.Contains("Review Saved 1", html);
        Assert.Contains("Review Saved 2", html);
        Assert.Contains("Keep separate", html);
        Assert.Contains("Skip account", html);
        Assert.Contains("Cancel sync", html);
        Assert.Contains("has not been saved", html);
    }

    [Fact]
    public async Task Import_merge_collects_a_confirmed_decision_without_saving_and_fixes_saved_survivor()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var preview = new ContactMergePreview(
            new() { Id = 123, Key = "edited", DisplayName = "Edited", XUserId = "old", XUsername = "old" },
            new() { Id = -1, Key = "(incoming — not saved)", DisplayName = "Incoming", XUserId = "new", XUsername = "new" },
            [Platform.X], "", "");
        var html = WebUtility.HtmlDecode(await host.RenderDialogAsync<MergeContactsDialog>(
            new() { [nameof(MergeContactsDialog.Preview)] = preview, [nameof(MergeContactsDialog.ImportMode)] = true }));
        Assert.Contains("incoming account is not saved", html);
        Assert.DoesNotContain("Surviving contact", html);
        Assert.Contains("Keep which X identity?", html);
        Assert.Contains("No incoming row or key", html);
        Assert.DoesNotContain("The other contact row is deleted", html);
        // If this accidentally uses saved-contact MergeAsync, nonexistent ID 123 fails.
        await host.RenderDialogAsync<MergeContactsDialog>(
            new() { [nameof(MergeContactsDialog.Preview)] = preview, [nameof(MergeContactsDialog.ImportMode)] = true },
            async dialog =>
            {
                typeof(MergeContactsDialog).GetMethod("SetChoice", PrivateInstance)!.Invoke(dialog, [Platform.X, -1]);
                typeof(MergeContactsDialog).GetField("confirmed", PrivateInstance)!.SetValue(dialog, true);
                Assert.True((bool)typeof(MergeContactsDialog).GetProperty("CanMerge", PrivateInstance)!.GetValue(dialog)!);
                await (Task)typeof(MergeContactsDialog).GetMethod("MergeAsync", PrivateInstance)!.Invoke(dialog, null)!;
                Assert.Null(typeof(MergeContactsDialog).GetField("error", PrivateInstance)!.GetValue(dialog));
            });
        Assert.Empty(await host.Provider.GetRequiredService<ContactService>().ListAsync());
        Assert.Equal(0, host.Http.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dialog_wait_finishes_on_cancel_or_navigation_disposal(bool dispose)
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var page = new Contacts();
        using var cancellation = new CancellationTokenSource();
        typeof(Contacts).GetField("syncCancellation", PrivateInstance)!.SetValue(page, cancellation);
        var dialog = new DialogReference(Guid.NewGuid(), host.Provider.GetRequiredService<IDialogService>());
        var waiting = (Task<DialogResult?>)typeof(Contacts).GetMethod("WaitForSyncDialogAsync", PrivateInstance)!
            .Invoke(page, [dialog, cancellation.Token])!;
        Assert.False(waiting.IsCompleted);
        if (dispose) page.Dispose();
        else typeof(Contacts).GetMethod("CancelSync", PrivateInstance)!.Invoke(page, null);
        try { await waiting.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
        Assert.True(waiting.IsCompleted);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Null(typeof(Contacts).GetField("syncDialog", PrivateInstance)!.GetValue(page));
    }

    [Fact]
    public async Task Cancellation_releases_sync_even_if_the_dialog_provider_has_not_finished_opening()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var opening = new TaskCompletionSource<IDialogReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = (Task<IDialogReference>)typeof(Contacts).GetMethod("AwaitSyncDialogOpenAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [opening.Task, cancellation.Token])!;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        // The provider may finish later; the cancelled sync never waits for that completion.
        opening.SetResult(new DialogReference(Guid.NewGuid(), host.Provider.GetRequiredService<IDialogService>()));
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("syncing")]
    [InlineData("merging")]
    [InlineData("scanningConflicts")]
    [InlineData("confirmingConflictScan")]
    [InlineData("disposed")]
    public async Task Busy_or_disposed_page_cannot_start_another_sync(string state)
    {
        var page = new Contacts();
        typeof(Contacts).GetField(state, PrivateInstance)!.SetValue(page, true);
        // An uninitialized page has no services: a correct guard returns before dialogs/network.
        await (Task)typeof(Contacts).GetMethod("ImportFollowsAsync", PrivateInstance)!.Invoke(page, [Platform.Bluesky])!;
    }
}
