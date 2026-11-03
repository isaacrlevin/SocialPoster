using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using SocialPoster.Core;

namespace SocialPoster.Components;

/// <summary>Configures navigation and default platforms for the SocialPoster UI.</summary>
public sealed class SocialPosterComponentOptions
{
    /// <summary>Default platforms for routed and standalone UI components. All are enabled by default.</summary>
    public IReadOnlyCollection<Platform> Platforms { get; set; } = PlatformSelection.All.Platforms;

    /// <summary>
    /// Gets or sets the mount path relative to the host's Blazor base URI, without leading/trailing
    /// slashes. The built-in routes use "socialposter". Custom paths require host-owned route wrappers.
    /// Use an empty string to mount at the host's root.
    /// </summary>
    public string BasePath { get; set; } = "socialposter";
}

/// <summary>Registers UI dependencies without changing the host's rendering or routing configuration.</summary>
public static class SocialPosterComponentServiceCollectionExtensions
{
    /// <summary>Registers UI options, navigation and MudBlazor services. Register Core separately.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configure">Optional navigation and platform configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSocialPosterComponents(
        this IServiceCollection services, Action<SocialPosterComponentOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<SocialPosterComponentOptions>()
            .Validate(o => o.Platforms is not null && o.Platforms.All(p => Enum.IsDefined(p)),
                "Platforms must contain only supported platforms.")
            .Validate(o => o.BasePath is not null && (o.BasePath.Length == 0 ||
                                                      o.BasePath.Split('/').All(segment => segment.Length > 0
                                                          && segment.All(c =>
                                                              char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))),
                "BasePath must be empty or slash-separated URL segments containing letters, digits, '-' or '_'.")
            .ValidateOnStart();
        if (configure is not null) options.Configure(configure);
        services.AddScoped<SocialPosterNavigation>();
        services.AddScoped<PlatformPreferences>();
        services.AddScoped<ConflictResultsStore>();
        services.AddMudServices();
        return services;
    }
}

/// <summary>Builds links relative to the host's base URI, including a configured UI mount path.</summary>
public sealed class SocialPosterNavigation(IOptions<SocialPosterComponentOptions> options)
{
    /// <summary>Returns the relative URI for a SocialPoster section, or Compose if omitted.</summary>
    /// <param name="section">A section such as contacts, history or settings.</param>
    /// <returns>A base-relative link. The root Compose link is "./".</returns>
    public string Link(string? section = null)
    {
        var path = options.Value.BasePath;
        return string.IsNullOrEmpty(section)
            ? (path.Length == 0 ? "./" : path)
            : (path.Length == 0 ? section : $"{path}/{section}");
    }
}