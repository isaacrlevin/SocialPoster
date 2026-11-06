namespace SocialPoster.Core;

/// <summary>Options for SocialPoster's dedicated, local single-user SQLite store.</summary>
public sealed class SocialPosterOptions
{
    /// <summary>Gets or sets the directory containing the database and Copilot agent working files.</summary>
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocialPoster");

    /// <summary>Gets or sets the database file name within <see cref="DataDirectory"/>.</summary>
    public string DatabaseFileName { get; set; } = "socialposter.db";

    /// <summary>Gets or sets the posting HTTP timeout, allowing large media uploads.</summary>
    public TimeSpan PostingTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Gets or sets whether the host runs on the user's own computer, which decides if local model servers
    /// (Ollama, LM Studio) and other localhost or private-network endpoints can be configured.
    /// <see cref="HostLocation.Auto"/> detects containers and cloud hosts; desktop apps set <see cref="HostLocation.Local"/>.
    /// </summary>
    public HostLocation Location { get; set; } = HostLocation.Auto;
}

/// <summary>Where the SocialPoster host process runs relative to the person using it.</summary>
public enum HostLocation
{
    /// <summary>Local unless the process looks like it runs in a container or a cloud host.</summary>
    Auto,

    /// <summary>On the user's own computer: localhost and private-network model servers are reachable and allowed.</summary>
    Local,

    /// <summary>Somewhere else (a server, container or cloud): localhost and private-network endpoints are refused.</summary>
    Remote,
}
