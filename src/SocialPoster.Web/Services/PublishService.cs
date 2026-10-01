using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SocialPoster.Core;
using SocialPoster.Web.Data;

namespace SocialPoster.Web.Services;

public sealed record PublishResult(Platform Platform, bool Success, string? Url, string? Error, bool OpensInBrowser = false);

public sealed record PlatformStatus(bool X, bool Bluesky, bool LinkedIn)
{
    public bool IsConnected(Platform p) => p switch
    {
        Platform.X => X,
        Platform.Bluesky => Bluesky,
        Platform.LinkedIn => LinkedIn,
        _ => false,
    };
}

public sealed class PublishService(
    ContactService contacts,
    BlueskyClient bluesky,
    XClient x,
    LinkedInClient linkedIn,
    SecretStore secrets,
    IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<PlatformStatus> GetStatusAsync() =>
        new(await IsXIntentEnabledAsync() || await x.IsConfiguredAsync(), await bluesky.IsConfiguredAsync(), await linkedIn.IsConfiguredAsync());

    /// <summary>True when X posts should go through a browser "intent" link instead of the paid X API.</summary>
    public async Task<bool> IsXIntentEnabledAsync() => await secrets.GetAsync(SettingKeys.XPostMethod) == "intent";

    /// <param name="templates">Template to publish for each selected platform.</param>
    public async Task<IReadOnlyList<PublishResult>> PublishAsync(string baseTemplate, IReadOnlyDictionary<Platform, string> templates, IReadOnlyList<MediaAttachment>? media = null, CancellationToken ct = default)
    {
        var keys = templates.Values.SelectMany(MentionTemplate.GetMentionKeys).ToList();
        var lookup = await contacts.GetByKeysAsync(keys, ct);
        Contact? Resolve(string key) => lookup.GetValueOrDefault(key);

        var xIntent = await IsXIntentEnabledAsync();
        media ??= [];

        var tasks = templates.Select(async kv =>
        {
            var rendered = PostRenderer.Render(kv.Key, kv.Value, Resolve);
            if (rendered.IsEmpty) return new PublishResult(kv.Key, false, null, "Post is empty.");
            if (rendered.IsOverLimit) return new PublishResult(kv.Key, false, null, $"Post is {rendered.Length}/{rendered.Limit} characters.");

            var mediaErrors = MediaRules.Validate(kv.Key, media);
            if (mediaErrors.Count > 0) return new PublishResult(kv.Key, false, null, string.Join(" ", mediaErrors));

            if (kv.Key == Platform.X && xIntent)
            {
                // No API call at all: hand the user a pre-filled composer and let them click Post themselves.
                // (Intent links can't carry files, so any media has to be attached by hand there.)
                return new PublishResult(Platform.X, true, XIntent.BuildUrl(rendered.PayloadText), null, OpensInBrowser: true);
            }

            try
            {
                var url = kv.Key switch
                {
                    Platform.X => await x.PostAsync(rendered, media, ct),
                    Platform.Bluesky => await bluesky.PostAsync(rendered, media, ct),
                    Platform.LinkedIn => await linkedIn.PostAsync(rendered, media, ct),
                    _ => throw new ArgumentOutOfRangeException(),
                };
                return new PublishResult(kv.Key, true, url, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or IOException)
            {
                return new PublishResult(kv.Key, false, null, ex.Message);
            }
        });

        var results = await Task.WhenAll(tasks);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.PostHistory.Add(new PostHistoryEntry { Template = baseTemplate, ResultsJson = JsonSerializer.Serialize(results) });
            await db.SaveChangesAsync(ct);
        }

        if (results.Any(r => r.Success))
        {
            await contacts.MarkUsedAsync(keys, ct);
        }

        return results;
    }

    public async Task<List<(PostHistoryEntry Entry, PublishResult[] Results)>> GetHistoryAsync(int take = 50)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entries = await db.PostHistory.AsNoTracking().OrderByDescending(p => p.Id).Take(take).ToListAsync();
        return entries.Select(e => (e, JsonSerializer.Deserialize<PublishResult[]>(e.ResultsJson) ?? [])).ToList();
    }
}
