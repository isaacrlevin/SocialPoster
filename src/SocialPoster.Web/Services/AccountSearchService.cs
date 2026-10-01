using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SocialPoster.Core;
using SocialPoster.Web.Data;

namespace SocialPoster.Web.Services;

public sealed record AccountSearchView(
    AccountSearchStatus Status,
    IReadOnlyList<AccountCandidate> Candidates,
    string? Error,
    string? Progress,
    string Query,
    DateTime? CompletedAtUtc);

/// <summary>Runs account searches in the background, caches the results per contact and notifies listeners when they change.</summary>
public sealed class AccountSearchService(
    AccountFinder finder,
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<AccountSearchService> logger)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly ConcurrentDictionary<int, string> _running = new();
    private readonly SemaphoreSlim _concurrency = new(2, 2);

    /// <summary>Raised with the contact id whenever a search starts, progresses or finishes. May be raised on any thread.</summary>
    public event Action<int>? Changed;

    public bool IsRunning(int contactId) => _running.ContainsKey(contactId);

    public async Task<AccountSearchView?> GetAsync(int contactId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AccountSearches.AsNoTracking().FirstOrDefaultAsync(s => s.Key == AccountSearch.KeyFor(contactId), ct);
        var progress = _running.GetValueOrDefault(contactId);
        if (row is null)
        {
            return progress is null ? null : new AccountSearchView(AccountSearchStatus.Running, [], null, progress, "", null);
        }

        var status = progress is not null ? AccountSearchStatus.Running
            : row.Status == AccountSearchStatus.Running ? AccountSearchStatus.Failed // interrupted by an app restart
            : row.Status;
        return new AccountSearchView(status, Deserialize(row.CandidatesJson), status == AccountSearchStatus.Failed ? row.Error ?? "The search was interrupted." : row.Error,
            progress, row.Query, row.CompletedAtUtc);
    }

    public async Task<Dictionary<int, AccountSearchView>> GetManyAsync(IEnumerable<int> contactIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, AccountSearchView>();
        foreach (var id in contactIds.Distinct())
        {
            if (await GetAsync(id, ct) is { } view) result[id] = view;
        }

        return result;
    }

    /// <summary>Starts a search unless one is already running for this contact. Returns immediately.</summary>
    public void Start(Contact contact, string? name = null, string? context = null)
    {
        var platforms = contact.MissingSearchablePlatforms();
        var request = new AccountSearchRequest(
            CleanName(string.IsNullOrWhiteSpace(name) ? contact.DisplayName : name),
            string.IsNullOrWhiteSpace(context) ? null : context.Trim(),
            platforms.Count > 0 ? platforms : [Platform.X, Platform.LinkedIn],
            contact.BlueskyHandle,
            contact.Bio,
            contact.XUsername,
            contact.LinkedInProfileUrl);

        if (!_running.TryAdd(contact.Id, "Waiting…"))
        {
            return;
        }

        Notify(contact.Id);
        _ = Task.Run(() => RunAsync(contact.Id, request));
    }

    /// <summary>Starts a search automatically if it's enabled, the contact is missing accounts and hasn't been searched before.</summary>
    public async Task StartIfNeededAsync(Contact contact, CancellationToken ct = default)
    {
        if (contact.Id == 0 || IsRunning(contact.Id) || contact.MissingSearchablePlatforms().Count == 0)
        {
            return;
        }

        var settings = await finder.GetSettingsAsync();
        if (!settings.Enabled || !settings.AutoSearch)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.AccountSearches.AnyAsync(s => s.Key == AccountSearch.KeyFor(contact.Id), ct))
        {
            Start(contact);
        }
    }

    /// <summary>Removes a candidate from the cached list (e.g. the user said "not them").</summary>
    public async Task DismissAsync(int contactId, AccountCandidate candidate, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AccountSearches.FirstOrDefaultAsync(s => s.Key == AccountSearch.KeyFor(contactId), ct);
        if (row is null) return;
        var list = Deserialize(row.CandidatesJson).Where(c => !(c.Platform == candidate.Platform && c.Handle == candidate.Handle)).ToList();
        row.CandidatesJson = JsonSerializer.Serialize(list, Json);
        await db.SaveChangesAsync(ct);
        Notify(contactId);
    }

    /// <summary>Removes emoji and decorations people add to display names, e.g. a trailing butterfly.</summary>
    internal static string CleanName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            if (System.Text.Rune.IsLetterOrDigit(rune) || System.Text.Rune.IsWhiteSpace(rune) || rune.Value is '.' or '\'' or '-' or '&' or ',')
            {
                sb.Append(rune.ToString());
            }
        }

        var clean = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return clean.Length == 0 ? name.Trim() : clean;
    }

    private async Task RunAsync(int contactId, AccountSearchRequest request)
    {
        var key = AccountSearch.KeyFor(contactId);
        try
        {
            await _concurrency.WaitAsync();
            try
            {
                await SaveAsync(key, row =>
                {
                    row.ContactId = contactId;
                    row.Query = request.Name + (request.Context is null ? "" : $" ({request.Context})");
                    row.Status = AccountSearchStatus.Running;
                    row.Error = null;
                    row.CreatedAtUtc = DateTime.UtcNow;
                    row.CompletedAtUtc = null;
                });

                var progress = new Progress<string>(line =>
                {
                    _running[contactId] = line;
                    Notify(contactId);
                });
                var (candidates, model) = await finder.FindAsync(request, progress);
                await SaveAsync(key, row =>
                {
                    row.Status = AccountSearchStatus.Done;
                    row.CandidatesJson = JsonSerializer.Serialize(candidates, Json);
                    row.Model = model;
                    row.CompletedAtUtc = DateTime.UtcNow;
                });
            }
            finally
            {
                _concurrency.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Account search for contact {ContactId} failed", contactId);
            await SaveAsync(key, row =>
            {
                row.ContactId = contactId;
                row.Query = string.IsNullOrEmpty(row.Query) ? request.Name : row.Query;
                row.Status = AccountSearchStatus.Failed;
                row.Error = ex.Message;
                row.CompletedAtUtc = DateTime.UtcNow;
            });
        }
        finally
        {
            _running.TryRemove(contactId, out _);
            Notify(contactId);
        }
    }

    private async Task SaveAsync(string key, Action<AccountSearch> update)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var row = await db.AccountSearches.FirstOrDefaultAsync(s => s.Key == key);
            if (row is null)
            {
                row = new AccountSearch { Key = key };
                db.AccountSearches.Add(row);
            }

            update(row);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Saving account search {Key} failed", key);
        }
    }

    private void Notify(int contactId)
    {
        try
        {
            Changed?.Invoke(contactId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Account search listener failed");
        }
    }

    private static List<AccountCandidate> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<AccountCandidate>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
