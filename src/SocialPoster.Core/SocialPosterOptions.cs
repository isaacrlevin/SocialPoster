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
}
