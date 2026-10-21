using System.Net;
using System.Text.Json;

namespace SocialPoster.Core.Services;

public static class AgentSettingKeys
{
    /// <summary>Id of the provider the account finder uses: <see cref="AgentSettingsStore.CopilotId"/> or a saved provider's id.</summary>
    public const string ActiveProvider = "agent.provider";

    /// <summary>JSON list of <see cref="AgentProvider"/>. Holds API keys, so it is only ever stored in the encrypted settings.</summary>
    public const string Providers = "agent.providers";

    /// <summary>JSON <see cref="SearchSettings"/> for the web search tool used by non-Copilot providers.</summary>
    public const string Search = "agent.search";
}

/// <summary>An OpenAI-compatible chat endpoint the account finder can run its agent on.</summary>
/// <param name="BaseUrl">API root, e.g. <c>https://api.openai.com/v1</c> or <c>http://localhost:11434/v1</c> for Ollama.</param>
/// <param name="ApiKey">Null or empty for local servers that don't need one.</param>
public sealed record AgentProvider(string Id, string Name, string BaseUrl, string? ApiKey, string Model);

public enum SearchKind
{
    None,
    Brave,
    Tavily,
    SearXNG,
}

/// <summary>Which web search service backs the <c>web_search</c> tool for non-Copilot providers.</summary>
/// <param name="Endpoint">Instance URL; only used by SearXNG.</param>
public sealed record SearchSettings(SearchKind Kind, string? Endpoint = null, string? ApiKey = null)
{
    public static SearchSettings Disabled { get; } = new(SearchKind.None);
}

/// <summary>Starting points for common OpenAI-compatible services; users can edit every field afterwards.</summary>
public sealed record AgentProviderPreset(string Name, string BaseUrl, string ModelExample, bool NeedsApiKey, string Hint);

public static class AgentProviderPresets
{
    public static readonly IReadOnlyList<AgentProviderPreset> All =
    [
        new("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", true, "Needs an API key from platform.openai.com."),
        new("Ollama (local)", "http://localhost:11434/v1", "llama3.1", false,
            "Runs on your PC, no key. The model must support tool calling (e.g. llama3.1, qwen2.5)."),
        new("LM Studio (local)", "http://localhost:1234/v1", "", false, "Start the local server in LM Studio and load a tool-capable model."),
        new("OpenRouter", "https://openrouter.ai/api/v1", "openai/gpt-4o-mini", true, "One key for many hosted models."),
        new("Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", true, "Fast hosted open models."),
        new("Azure OpenAI", "https://YOUR-RESOURCE.openai.azure.com/openai/v1", "your-deployment-name", true,
            "Use your resource's /openai/v1 URL and the deployment name as the model."),
        new("Custom", "", "", false, "Any server that speaks the OpenAI chat completions API."),
    ];
}

/// <summary>Reads and writes the account finder's provider list, active provider and search settings.</summary>
public sealed class AgentSettingsStore(SecretStore secrets)
{
    public const string CopilotId = "copilot";
    public const int MaxProviders = 20;

    public async Task<IReadOnlyList<AgentProvider>> GetProvidersAsync(CancellationToken ct = default)
    {
        var json = await secrets.GetAsync(AgentSettingKeys.Providers, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<AgentProvider>>(json) ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p.Id) && p.Id != CopilotId).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The selected provider id; falls back to Copilot when unset or when the saved provider was removed.</summary>
    public async Task<string> GetActiveIdAsync(CancellationToken ct = default)
    {
        var id = await secrets.GetAsync(AgentSettingKeys.ActiveProvider, ct);
        if (string.IsNullOrWhiteSpace(id) || id == CopilotId) return CopilotId;
        return (await GetProvidersAsync(ct)).Any(p => p.Id == id) ? id : CopilotId;
    }

    public async Task<AgentProvider?> GetActiveProviderAsync(CancellationToken ct = default)
    {
        var id = await GetActiveIdAsync(ct);
        return id == CopilotId ? null : (await GetProvidersAsync(ct)).FirstOrDefault(p => p.Id == id);
    }

    public async Task SetActiveAsync(string id, CancellationToken ct = default)
    {
        if (id != CopilotId && (await GetProvidersAsync(ct)).All(p => p.Id != id))
            throw new InvalidOperationException("That provider no longer exists.");
        await secrets.SetAsync(AgentSettingKeys.ActiveProvider, id == CopilotId ? null : id, ct);
    }

    /// <summary>
    /// Adds a provider (empty <c>Id</c>) or replaces the one with the same id. A blank API key keeps the saved key.
    /// </summary>
    /// <returns>The saved provider, including its generated id.</returns>
    public async Task<AgentProvider> SaveProviderAsync(AgentProvider provider, CancellationToken ct = default)
    {
        var list = (await GetProvidersAsync(ct)).ToList();
        var existing = list.FirstOrDefault(p => p.Id == provider.Id);
        provider = Normalize(provider) with
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
        };
        if (string.IsNullOrEmpty(provider.ApiKey)) provider = provider with { ApiKey = existing?.ApiKey };
        if (Validate(provider) is { } error) throw new InvalidOperationException(error);
        if (list.Any(p => p.Id != provider.Id && string.Equals(p.Name, provider.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A provider named '{provider.Name}' already exists.");
        if (existing is null && list.Count >= MaxProviders)
            throw new InvalidOperationException($"You can save at most {MaxProviders} providers.");

        if (existing is null) list.Add(provider);
        else list[list.IndexOf(existing)] = provider;
        await secrets.SetAsync(AgentSettingKeys.Providers, JsonSerializer.Serialize(list), ct);
        return provider;
    }

    public async Task RemoveProviderAsync(string id, CancellationToken ct = default)
    {
        var list = (await GetProvidersAsync(ct)).ToList();
        if (list.RemoveAll(p => p.Id == id) == 0) return;
        await secrets.SetAsync(AgentSettingKeys.Providers, list.Count == 0 ? null : JsonSerializer.Serialize(list), ct);
        if (await secrets.GetAsync(AgentSettingKeys.ActiveProvider, ct) == id)
            await secrets.SetAsync(AgentSettingKeys.ActiveProvider, null, ct);
    }

    public async Task<SearchSettings> GetSearchAsync(CancellationToken ct = default)
    {
        var json = await secrets.GetAsync(AgentSettingKeys.Search, ct);
        if (string.IsNullOrWhiteSpace(json)) return SearchSettings.Disabled;
        try
        {
            var value = JsonSerializer.Deserialize<SearchSettings>(json);
            return value is not null && Enum.IsDefined(value.Kind) ? value : SearchSettings.Disabled;
        }
        catch (JsonException)
        {
            return SearchSettings.Disabled;
        }
    }

    /// <summary>Saves the search settings. A blank API key keeps the saved one when the service is unchanged.</summary>
    public async Task<SearchSettings> SaveSearchAsync(SearchSettings search, CancellationToken ct = default)
    {
        search = search with
        {
            Endpoint = string.IsNullOrWhiteSpace(search.Endpoint) ? null : search.Endpoint.Trim(),
            ApiKey = string.IsNullOrWhiteSpace(search.ApiKey) ? null : search.ApiKey.Trim(),
        };
        if (search.ApiKey is null && search.Kind is SearchKind.Brave or SearchKind.Tavily)
        {
            var old = await GetSearchAsync(ct);
            if (old.Kind == search.Kind) search = search with { ApiKey = old.ApiKey };
        }
        if (Validate(search) is { } error) throw new InvalidOperationException(error);
        if (search.Kind != SearchKind.SearXNG) search = search with { Endpoint = null };
        if (search.Kind == SearchKind.SearXNG) search = search with { ApiKey = null };
        await secrets.SetAsync(AgentSettingKeys.Search,
            search.Kind == SearchKind.None ? null : JsonSerializer.Serialize(search), ct);
        return search;
    }

    public static AgentProvider Normalize(AgentProvider p) => p with
    {
        Id = p.Id?.Trim() ?? "",
        Name = p.Name?.Trim() ?? "",
        BaseUrl = (p.BaseUrl?.Trim() ?? "").TrimEnd('/'),
        ApiKey = string.IsNullOrWhiteSpace(p.ApiKey) ? null : p.ApiKey.Trim(),
        Model = p.Model?.Trim() ?? "",
    };

    /// <returns>An error message, or null when the provider is usable.</returns>
    public static string? Validate(AgentProvider provider)
    {
        var p = Normalize(provider);
        if (p.Name.Length == 0) return "Give the provider a name.";
        if (p.Name.Length > 60) return "Provider names are at most 60 characters.";
        if (p.Name.Equals("GitHub Copilot", StringComparison.OrdinalIgnoreCase)) return "That name is reserved for the built-in Copilot provider.";
        if (p.Model.Length == 0) return "Enter the model (or deployment) name to use.";
        if (p.ApiKey is not null && p.ApiKey.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)))
            return "API keys can't contain spaces or control characters.";
        return ValidateEndpoint(p.BaseUrl, "Base URL", p.ApiKey is not null);
    }

    /// <returns>An error message, or null when the search settings are usable.</returns>
    public static string? Validate(SearchSettings search)
    {
        if (!Enum.IsDefined(search.Kind)) return "Choose a search provider.";
        if (search.Kind is SearchKind.Brave or SearchKind.Tavily && string.IsNullOrWhiteSpace(search.ApiKey))
            return $"{search.Kind} needs an API key.";
        if (search.ApiKey is not null && search.ApiKey.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)))
            return "API keys can't contain spaces or control characters.";
        if (search.Kind == SearchKind.SearXNG)
            return ValidateEndpoint(search.Endpoint?.Trim().TrimEnd('/') ?? "", "SearXNG URL", false);
        return null;
    }

    /// <summary>HTTPS is required unless the server is on this machine or a private network.</summary>
    private static string? ValidateEndpoint(string url, string label, bool hasSecret)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return $"{label} must be an http(s) URL without credentials, query or fragment.";
        if (uri.Scheme == "http" && !IsLocalHost(uri.Host))
            return $"{label} must use https unless the server is on this computer or your local network.";
        return null;
    }

    internal static bool IsLocalHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return !SafeWebFetcher.IsPublicAddress(ip);
        return !host.Contains('.');
    }
}
