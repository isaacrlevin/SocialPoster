using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SocialPoster.Core;
using SocialPoster.Core.Data;

namespace SocialPoster.Core.Services;

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

    private readonly ConcurrentDictionary<string, string> _running = new();
    private readonly SemaphoreSlim _concurrency = new(2, 2);

    /// <summary>Raised with the contact id whenever a search starts, progresses or finishes. May be raised on any thread.</summary>
    public event Action<int>? Changed;

    public bool IsRunning(int contactId, IReadOnlyCollection<Platform>? platforms = null) => _running.ContainsKey(SearchKey(contactId, platforms));

    public async Task<AccountSearchView?> GetAsync(int contactId, CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null)
    {
        var key = SearchKey(contactId, platforms);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AccountSearches.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        var progress = _running.GetValueOrDefault(key);
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

    public async Task<Dictionary<int, AccountSearchView>> GetManyAsync(IEnumerable<int> contactIds, CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null)
    {
        var result = new Dictionary<int, AccountSearchView>();
        foreach (var id in contactIds.Distinct())
        {
            if (await GetAsync(id, ct, platforms) is { } view) result[id] = view;
        }

        return result;
    }

    /// <summary>Starts a search unless one is already running for this contact and platform selection. Returns immediately.</summary>
    public void Start(Contact contact, string? name = null, string? context = null, IReadOnlyCollection<Platform>? platforms = null)
    {
        var request = CreateRequest(contact, name, context, platforms);
        if (request.Platforms.Count == 0) return;
        var key = SearchKey(contact.Id, platforms);

        if (!_running.TryAdd(key, "Waiting…"))
        {
            return;
        }

        Notify(contact.Id);
        _ = Task.Run(() => RunAsync(contact.Id, contact.DiscoveryRevision, key, request));
    }

    internal static AccountSearchRequest CreateRequest(Contact contact, string? name, string? context, IReadOnlyCollection<Platform>? platforms)
    {
        var selection = platforms is null ? PlatformSelection.All : new PlatformSelection(platforms);
        var missing = contact.MissingSearchablePlatforms().Where(selection.Contains).ToArray();
        return new AccountSearchRequest(
            CleanName(string.IsNullOrWhiteSpace(name) ? contact.DisplayName : name),
            string.IsNullOrWhiteSpace(context) ? null : context.Trim(),
            missing.Length > 0 ? missing : selection.Searchable,
            selection.Contains(Platform.Bluesky) ? contact.BlueskyHandle : null,
            contact.Bio,
            selection.Contains(Platform.X) ? contact.XUsername : null,
            selection.Contains(Platform.LinkedIn) ? contact.LinkedInProfileUrl : null);
    }

    /// <summary>Starts a search automatically if enabled, the contact is missing an enabled account and this platform selection hasn't been searched before.</summary>
    public async Task StartIfNeededAsync(Contact contact, CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null)
    {
        var selection = platforms is null ? PlatformSelection.All : new PlatformSelection(platforms);
        if (contact.Id == 0 || IsRunning(contact.Id, platforms) || !contact.MissingSearchablePlatforms().Any(selection.Contains))
        {
            return;
        }

        var settings = await finder.GetSettingsAsync();
        if (!settings.Enabled || !settings.AutoSearch)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = SearchKey(contact.Id, platforms);
        if (!await db.AccountSearches.AnyAsync(s => s.Key == key, ct))
        {
            Start(contact, platforms: platforms);
        }
    }

    /// <summary>Removes a candidate from the cached list (e.g. the user said "not them").</summary>
    public async Task DismissAsync(int contactId, AccountCandidate candidate, CancellationToken ct = default, IReadOnlyCollection<Platform>? platforms = null)
    {
        var key = SearchKey(contactId, platforms);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AccountSearches.FirstOrDefaultAsync(s => s.Key == key, ct);
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

    internal static string SearchKey(int contactId, IReadOnlyCollection<Platform>? platforms)
    {
        var searchable = (platforms is null ? PlatformSelection.All : new PlatformSelection(platforms)).Searchable.Order().ToArray();
        var key = AccountSearch.KeyFor(contactId);
        // Preserve legacy results only for the original two-platform search.
        return searchable.SequenceEqual(new[] { Platform.X, Platform.LinkedIn })
            ? key : $"{key}:{string.Join("-", searchable)}";
    }

    private async Task RunAsync(int contactId, int revision, string key, AccountSearchRequest request)
    {
        try
        {
            await _concurrency.WaitAsync();
            try
            {
                await SaveAsync(key, contactId, revision, row =>
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
                    _running[key] = line;
                    Notify(contactId);
                });
                var (candidates, model) = await finder.FindAsync(request, progress);
                await SaveAsync(key, contactId, revision, row =>
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
            await SaveAsync(key, contactId, revision, row =>
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
            _running.TryRemove(key, out _);
            Notify(contactId);
        }
    }

    internal async Task SaveAsync(string key, int contactId, int revision, Action<AccountSearch> update)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            if (!await db.Contacts.AnyAsync(c => c.Id == contactId && c.DiscoveryRevision == revision))
                return; // Maintenance invalidated this in-flight search (or removed its source).
            var row = await db.AccountSearches.FirstOrDefaultAsync(s => s.Key == key);
            if (row is null)
            {
                row = new AccountSearch { Key = key };
                db.AccountSearches.Add(row);
            }

            update(row);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
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
