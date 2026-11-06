using Microsoft.Extensions.Options;

namespace SocialPoster.Core.Services;

/// <summary>
/// Whether SocialPoster runs on the user's own computer. When it doesn't, "localhost" is the server rather than the
/// user's machine, so Ollama and LM Studio can't be configured, and the server must not be pointed at its own loopback
/// or private network (that would let a signed-in user probe the hosting network).
/// </summary>
public sealed class HostLocality(IOptions<SocialPosterOptions> options)
{
    /// <summary>True when local model servers and other localhost or private-network endpoints may be used.</summary>
    public bool IsLocal { get; } = Resolve(options.Value.Location, Environment.GetEnvironmentVariable);

    /// <summary>Applies the configured <see cref="HostLocation"/>, detecting the environment only for <c>Auto</c>.</summary>
    internal static bool Resolve(HostLocation location, Func<string, string?> env) => location switch
    {
        HostLocation.Local => true,
        HostLocation.Remote => false,
        _ => !LooksHosted(env),
    };

    // Variables that container runtimes and common PaaS hosts set. Not exhaustive, so hosts that aren't detected
    // should set SocialPoster:Location=Remote.
    private static readonly string[] HostedMarkers =
    [
        "DOTNET_RUNNING_IN_CONTAINER", "KUBERNETES_SERVICE_HOST", "WEBSITE_SITE_NAME", "WEBSITE_INSTANCE_ID",
        "CONTAINER_APP_NAME", "AWS_EXECUTION_ENV", "ECS_CONTAINER_METADATA_URI_V4", "K_SERVICE", "FLY_APP_NAME",
        "RAILWAY_ENVIRONMENT", "RENDER", "DYNO", "VERCEL",
    ];

    private static bool LooksHosted(Func<string, string?> env) =>
        HostedMarkers.Any(name => !string.IsNullOrEmpty(env(name)));
}
