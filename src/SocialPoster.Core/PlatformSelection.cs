namespace SocialPoster.Core;

/// <summary>An immutable, validated selection of platforms supported by the app.</summary>
public sealed class PlatformSelection
{
    /// <summary>The default selection, containing all supported platforms.</summary>
    public static PlatformSelection All { get; } = new(Enum.GetValues<Platform>());

    /// <summary>Creates a selection, preserving order and removing duplicates.</summary>
    /// <param name="platforms">Platforms to enable. An empty collection disables all platforms.</param>
    public PlatformSelection(IEnumerable<Platform> platforms)
    {
        ArgumentNullException.ThrowIfNull(platforms);
        var values = platforms.Distinct().ToArray();
        if (values.Any(p => !Enum.IsDefined(p)))
            throw new ArgumentException("Only supported platforms may be selected.", nameof(platforms));
        Platforms = Array.AsReadOnly(values);
        Searchable = Array.AsReadOnly(values.Where(p => p is Platform.X or Platform.LinkedIn).ToArray());
    }

    /// <summary>The enabled platforms.</summary>
    public IReadOnlyList<Platform> Platforms { get; }

    /// <summary>Enabled platforms supported by the Copilot account finder.</summary>
    public IReadOnlyList<Platform> Searchable { get; }

    /// <summary>Checks whether a platform is enabled.</summary>
    /// <param name="platform">The platform to check.</param>
    /// <returns>True when the platform is enabled.</returns>
    public bool Contains(Platform platform) => Platforms.Contains(platform);
}
