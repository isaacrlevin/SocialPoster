using System.ComponentModel;
using System.Text;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using SocialPoster.Core;

namespace SocialPoster.Web.Services;

/// <summary>What the agent knows about the person or organization being searched for.</summary>
public sealed record AccountSearchRequest(
    string Name,
    string? Context,
    IReadOnlyList<Platform> Platforms,
    string? BlueskyHandle = null,
    string? Bio = null,
    string? KnownXUsername = null,
    string? KnownLinkedInUrl = null);

public sealed record AccountFinderSettings(bool Enabled, bool AutoSearch, string Model);

/// <summary>Result of asking the Copilot agent to check whether a specific X or LinkedIn account is real.</summary>
public sealed record VerifiedAccount(bool Exists, string? Name, string? Reason);

/// <summary>Runs a GitHub Copilot agent (via the Copilot SDK) that searches the web for someone's X and LinkedIn accounts.</summary>
public sealed class AccountFinder(SecretStore secrets, ILogger<AccountFinder> logger, IConfiguration config) : IAsyncDisposable
{
    public const string DefaultModel = "claude-haiku-4.5";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly SemaphoreSlim _startLock = new(1, 1);
    private CopilotClient? _client;
    private string? _clientToken;

    public async Task<AccountFinderSettings> GetSettingsAsync()
    {
        var s = await secrets.GetManyAsync(SettingKeys.CopilotEnabled, SettingKeys.CopilotAutoSearch, SettingKeys.CopilotModel);
        return new AccountFinderSettings(
            s[SettingKeys.CopilotEnabled] != "false",
            s[SettingKeys.CopilotAutoSearch] != "false",
            string.IsNullOrWhiteSpace(s[SettingKeys.CopilotModel]) ? DefaultModel : s[SettingKeys.CopilotModel]!);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        return (await client.ListModelsAsync(ct)).Select(m => m.Id).ToList();
    }

    /// <param name="progress">Receives short status lines such as the search queries being run.</param>
    public async Task<(List<AccountCandidate> Candidates, string Model)> FindAsync(AccountSearchRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync();
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("The Copilot account finder is turned off under Accounts.");
        }

        var client = await GetClientAsync(ct);
        var reported = new TaskCompletionSource<ReportedCandidate[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var report = AIFunctionFactory.Create(
            ([Description("All candidate accounts found, best first. Empty if none.")] ReportedCandidate[] candidates) =>
            {
                reported.TrySetResult(candidates ?? []);
                return "Recorded. You are done; do not call any more tools.";
            },
            "report_candidates",
            "Report the candidate accounts you found. Call this exactly once, at the end.");

        var workDir = Path.Combine(config["SocialPoster:DataDirectory"] ?? Path.GetTempPath(), "agent");
        Directory.CreateDirectory(workDir);

        var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = settings.Model,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Tools = [report],
            AvailableTools = ["web_search", "web_fetch", "report_candidates"],
            WorkingDirectory = workDir,
            SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Replace, Content = SystemPrompt },
        });

        try
        {
            using var sub = session.On<SessionEvent>(evt =>
            {
                switch (evt)
                {
                    case ToolExecutionStartEvent t when t.Data.ToolName is "web_search" or "web_fetch":
                        logger.LogDebug("Account finder tool {Tool}: {Args}", t.Data.ToolName, t.Data.Arguments);
                        progress?.Report(Describe(t.Data.ToolName, t.Data.Arguments));
                        break;
                    case SessionErrorEvent e:
                        failed.TrySetResult(e.Data.Message ?? "The Copilot agent failed.");
                        break;
                    case SessionIdleEvent:
                        failed.TrySetResult("The agent finished without reporting any results.");
                        break;
                }
            });

            progress?.Report($"Asking Copilot ({settings.Model})…");
            await session.SendAsync(BuildPrompt(request), ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var winner = await Task.WhenAny(reported.Task, failed.Task, Task.Delay(System.Threading.Timeout.Infinite, timeout.Token));

            if (winner == reported.Task)
            {
                logger.LogDebug("Account finder reported for {Name}: {Json}", request.Name, System.Text.Json.JsonSerializer.Serialize(reported.Task.Result));
                return (AccountCandidates.Normalize(reported.Task.Result).Where(c => request.Platforms.Contains(c.Platform)).ToList(), settings.Model);
            }

            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(winner == failed.Task ? Explain(failed.Task.Result) : $"The search took longer than {Timeout.TotalMinutes:0} minutes.");
        }
        finally
        {
            await CleanUpAsync(client, session);
        }
    }

    /// <summary>
    /// Asks the Copilot agent to check whether a specific X username or LinkedIn profile URL is real.
    /// Unlike the X API lookup, this doesn't need X credentials or count against the X API quota, and it's the only
    /// way to check a LinkedIn profile at all since the API has no lookup for other members.
    /// </summary>
    /// <param name="identifier">An X username (with or without '@') or a LinkedIn profile URL.</param>
    /// <param name="progress">Receives short status lines such as the search queries being run.</param>
    public async Task<VerifiedAccount> VerifyAsync(Platform platform, string identifier, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync();
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("The Copilot account finder is turned off under Accounts.");
        }

        identifier = identifier.Trim();
        var client = await GetClientAsync(ct);
        var reported = new TaskCompletionSource<VerifiedAccount>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var report = AIFunctionFactory.Create(
            ([Description("True only if you found solid evidence this is a real, currently active account or profile.")] bool exists,
             [Description("Exact display name shown on the profile, if you found it. Null if not found.")] string? name,
             [Description("One short sentence: the evidence for your conclusion.")] string? reason) =>
            {
                reported.TrySetResult(new VerifiedAccount(exists, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()));
                return "Recorded. You are done; do not call any more tools.";
            },
            "report_verification",
            "Report whether the account exists. Call this exactly once, at the end.");

        var workDir = Path.Combine(config["SocialPoster:DataDirectory"] ?? Path.GetTempPath(), "agent");
        Directory.CreateDirectory(workDir);

        var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = settings.Model,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Tools = [report],
            AvailableTools = ["web_search", "web_fetch", "report_verification"],
            WorkingDirectory = workDir,
            SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Replace, Content = VerifySystemPrompt },
        });

        try
        {
            using var sub = session.On<SessionEvent>(evt =>
            {
                switch (evt)
                {
                    case ToolExecutionStartEvent t when t.Data.ToolName is "web_search" or "web_fetch":
                        logger.LogDebug("Account verify tool {Tool}: {Args}", t.Data.ToolName, t.Data.Arguments);
                        progress?.Report(Describe(t.Data.ToolName, t.Data.Arguments));
                        break;
                    case SessionErrorEvent e:
                        failed.TrySetResult(e.Data.Message ?? "The Copilot agent failed.");
                        break;
                    case SessionIdleEvent:
                        failed.TrySetResult("The agent finished without reporting a result.");
                        break;
                }
            });

            progress?.Report($"Asking Copilot ({settings.Model})…");
            var prompt = platform == Platform.X
                ? $"Check whether the X (Twitter) account @{identifier.TrimStart('@')} is a real, currently active account. Fetch https://x.com/{identifier.TrimStart('@')} directly first. Report the exact display name if you can find it."
                : $"Check whether this LinkedIn profile is real: {identifier}. Fetch that exact URL directly first. Report the exact name shown on the profile.";
            await session.SendAsync(prompt, ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var winner = await Task.WhenAny(reported.Task, failed.Task, Task.Delay(System.Threading.Timeout.Infinite, timeout.Token));

            if (winner == reported.Task)
            {
                logger.LogDebug("Account verification for {Platform} {Identifier}: {Result}", platform, identifier, reported.Task.Result);
                return reported.Task.Result;
            }

            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(winner == failed.Task ? Explain(failed.Task.Result) : $"The check took longer than {Timeout.TotalMinutes:0} minutes.");
        }
        finally
        {
            await CleanUpAsync(client, session);
        }
    }

    private async Task CleanUpAsync(CopilotClient client, CopilotSession session)
    {
        var id = session.SessionId;
        try
        {
            await session.AbortAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Abort failed");
        }

        await session.DisposeAsync();
        try
        {
            // Keep these throwaway sessions out of the user's Copilot session history.
            await client.DeleteSessionAsync(id);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Deleting Copilot session {Id} failed", id);
        }
    }

    private async Task<CopilotClient> GetClientAsync(CancellationToken ct)
    {
        var token = await secrets.GetAsync(SettingKeys.CopilotGitHubToken, ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            // The SDK doesn't pick up the Copilot CLI's own sign-in, so find that token ourselves.
            token = CopilotCliToken.Find(logger);
        }

        await _startLock.WaitAsync(ct);
        try
        {
            if (_client is not null && _clientToken == token)
            {
                return _client;
            }

            if (_client is not null)
            {
                await _client.DisposeAsync();
                _client = null;
            }

            var client = new CopilotClient(new CopilotClientOptions
            {
                GitHubToken = string.IsNullOrWhiteSpace(token) ? null : token,
            });
            try
            {
                await client.StartAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await client.DisposeAsync();
                throw new InvalidOperationException(
                    $"Couldn't start GitHub Copilot: {ex.Message}. Sign in with the GitHub Copilot app or CLI (copilot login), or add a GitHub token under Accounts.", ex);
            }

            _client = client;
            _clientToken = token;
            return client;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private static string Explain(string message) =>
        message.Contains("No GitHub OAuth token", StringComparison.OrdinalIgnoreCase) || message.Contains("HMAC key", StringComparison.OrdinalIgnoreCase)
            ? $"{message} Sign in by running \"copilot login\" in a terminal, or paste a GitHub token under Accounts → Copilot account finder."
            : message;

    private static string Describe(string tool, object? args)
    {
        var text = args?.ToString() ?? "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            foreach (var name in new[] { "query", "url" })
            {
                if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    text = v.GetString() ?? text;
                    break;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        if (text.Length > 90) text = text[..90] + "…";
        return tool == "web_search" ? $"Searching: {text}" : $"Reading: {text}";
    }

    internal static string BuildPrompt(AccountSearchRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Find the {string.Join(" and ", r.Platforms.Select(p => p == Platform.X ? "X (Twitter)" : "LinkedIn"))} account(s) for this person or organization.");
        sb.AppendLine();
        sb.AppendLine($"Name: {r.Name}");
        if (!string.IsNullOrWhiteSpace(r.Context)) sb.AppendLine($"Context from the user: {r.Context}");
        if (!string.IsNullOrWhiteSpace(r.BlueskyHandle)) sb.AppendLine($"Bluesky: @{r.BlueskyHandle}");
        if (!string.IsNullOrWhiteSpace(r.Bio)) sb.AppendLine($"Bio: {r.Bio.ReplaceLineEndings(" ")}");
        if (!string.IsNullOrWhiteSpace(r.KnownXUsername)) sb.AppendLine($"Known X account: https://x.com/{r.KnownXUsername}");
        if (!string.IsNullOrWhiteSpace(r.KnownLinkedInUrl)) sb.AppendLine($"Known LinkedIn: {r.KnownLinkedInUrl}");
        return sb.ToString();
    }

    private const string SystemPrompt = """
        You are an assistant inside a social media posting app. You find the X (Twitter) and LinkedIn accounts
        that belong to a given person or organization, so the user can @mention them correctly.

        Tools:
        - web_search answers a natural-language question using live web results and cites its sources. Ask direct questions such as
          "What is Jane Doe's X (Twitter) handle? She is CEO of Contoso." or "LinkedIn profile URL for Jane Doe, CEO of Contoso".
          Search once per platform first; refine only if needed.
        - web_fetch reads a page. Use it only for personal or company websites (they often link their social profiles).
          Never fetch x.com, twitter.com or linkedin.com: they require login.
        - Use at most 6 tool calls in total.

        Reporting:
        - A handle or profile URL that appears in a web_search answer or its cited sources counts as found. Report it.
          X profile URLs look like https://x.com/<handle>. LinkedIn: https://www.linkedin.com/in/<slug> (people) or https://www.linkedin.com/company/<slug> (organizations).
        - Do not invent handles that no source mentioned.
        - Include plausible namesakes with low confidence (up to 5 per platform) so the user can choose.
        - For organizations, include the numeric LinkedIn organization id if a source shows it (e.g. linkedin.com/company/1035).
        - Confidence: 90+ when an official source links the profile or it clearly matches the bio; 60-89 when name and role/employer match; below 60 for weak matches.
        - Finish by calling report_candidates exactly once (with an empty list only if nothing at all was found). Do not write a text answer.
        """;

    private const string VerifySystemPrompt = """
        You are an assistant inside a social media posting app. You check whether one specific, already-known X
        (Twitter) account or LinkedIn profile URL is real, so the user can trust an @mention before posting.
        Unlike a general search, you are given the exact handle or URL to check, so check that URL itself first:

        1. Call web_fetch on the exact URL you were given (build https://x.com/<handle> for an X handle). X and
           LinkedIn both return real page content (title, bio/description, follower count) to a logged-out fetch
           for public profiles and company pages; a missing account instead returns a 404 or an explicit "page
           isn't available" / "doesn't exist" message. A login wall alone (with no name or details) is inconclusive,
           not evidence the account is fake.
        2. If the fetch shows a real name, bio, description or follower count for that specific handle/URL, report
           exists = true with the exact name.
        3. If the fetch clearly 404s, or the page explicitly says the profile/page doesn't exist or was removed,
           report exists = false.
        4. Only if step 1 is inconclusive, make at most one web_search call asking specifically about that handle
           or URL (e.g. "site:linkedin.com/company/acme" or "Who is the X account @jack?") to confirm one way or the
           other.
        - Use at most 3 tool calls in total.

        Reporting:
        - Base your answer only on what the fetch or search actually showed for that specific handle/URL. Do not
          guess, and do not report false just because a search turned up nothing when the direct fetch succeeded.
        - Finish by calling report_verification exactly once. Do not write a text answer.
        """;

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        _startLock.Dispose();
    }
}
