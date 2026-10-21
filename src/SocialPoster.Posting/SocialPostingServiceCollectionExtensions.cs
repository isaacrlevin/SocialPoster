using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SocialPoster.Posting.Services;

namespace SocialPoster.Posting;

/// <summary>Options for the posting HTTP clients.</summary>
public sealed class SocialPostingOptions
{
    /// <summary>Gets or sets the posting HTTP timeout, allowing large media uploads.</summary>
    public TimeSpan PostingTimeout { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>Registers the standalone posting services.</summary>
public static class SocialPostingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SocialPublisher"/> and the five platform clients with their HTTP clients.
    /// Credentials come from the <see cref="ICredentialStore"/> you register; when none is registered an
    /// <see cref="InMemoryCredentialStore"/> is used.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configure">Optional HTTP configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSocialPosting(
        this IServiceCollection services, Action<SocialPostingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<SocialPostingOptions>()
            .Validate(o => o.PostingTimeout > TimeSpan.Zero
                && o.PostingTimeout.TotalMilliseconds <= int.MaxValue,
                "PostingTimeout must be positive and within HttpClient's supported range.")
            .ValidateOnStart();
        if (configure is not null) options.Configure(configure);

        services.AddLogging();
        services.TryAddSingleton<ICredentialStore, InMemoryCredentialStore>();
        services.AddHttpClient<BlueskyClient>(ConfigurePostingClient)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
            });
        services.AddHttpClient<XClient>(ConfigurePostingClient);
        services.AddHttpClient<LinkedInClient>(ConfigurePostingClient);
        // Credentials must never follow a redirect to another host.
        services.AddHttpClient<MastodonClient>(ConfigurePostingClient)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<ThreadsClient>(ConfigurePostingClient)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        // Transient: the typed clients hold an HttpClient, which must not be captured for the app's lifetime.
        services.TryAddTransient<SocialPublisher>();
        return services;
    }

    private static void ConfigurePostingClient(IServiceProvider services, HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SocialPoster/1.0");
        client.Timeout = services.GetRequiredService<IOptions<SocialPostingOptions>>().Value.PostingTimeout;
    }
}
