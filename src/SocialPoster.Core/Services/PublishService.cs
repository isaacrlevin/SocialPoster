using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SocialPoster.Core;
using SocialPoster.Core.Data;

namespace SocialPoster.Core.Services;

/// <summary>Publishes through <see cref="SocialPublisher"/>, resolving mentions from saved contacts and keeping history.</summary>
public sealed class PublishService(
    ContactService contacts,
    SocialPublisher publisher,
    IDbContextFactory<AppDbContext> dbFactory)
{
    public Task<PlatformStatus> GetStatusAsync(IReadOnlyCollection<Platform>? platforms = null) =>
        publisher.GetStatusAsync(platforms);

    /// <summary>True when X posts should go through a browser "intent" link instead of the paid X API.</summary>
    public Task<bool> IsXIntentEnabledAsync() => publisher.IsXIntentEnabledAsync();

    /// <param name="templates">Template to publish for each selected platform.</param>
    public async Task<IReadOnlyList<PublishResult>> PublishAsync(string baseTemplate, IReadOnlyDictionary<Platform, string> templates, IReadOnlyList<MediaAttachment>? media = null, CancellationToken ct = default)
    {
        var keys = templates.Values.SelectMany(MentionTemplate.GetMentionKeys).ToList();
        var lookup = await contacts.GetByKeysAsync(keys, ct);

        var results = await publisher.PublishAsync(templates, key => lookup.GetValueOrDefault(key), media, ct);

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
