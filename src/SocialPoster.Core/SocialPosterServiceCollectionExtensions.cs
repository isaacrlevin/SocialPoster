using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SocialPoster.Core.Data;
using SocialPoster.Core.Services;

namespace SocialPoster.Core;

/// <summary>Registers and initializes the reusable SocialPoster backend.</summary>
public static class SocialPosterServiceCollectionExtensions
{
    /// <summary>
    /// Registers SQLite storage, protected settings and platform services. Does not create files,
    /// initialize the database, or change the host's Data Protection identity or key persistence.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configure">Optional storage and HTTP configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSocialPosterCore(
        this IServiceCollection services, Action<SocialPosterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<SocialPosterOptions>()
            .Validate(o => !string.IsNullOrWhiteSpace(o.DataDirectory), "DataDirectory must not be empty.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.DatabaseFileName)
                && o.DatabaseFileName == Path.GetFileName(o.DatabaseFileName)
                && o.DatabaseFileName is not "." and not ".."
                && o.DatabaseFileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
                "DatabaseFileName must be a file name, not a path.")
            .Validate(o => o.PostingTimeout > TimeSpan.Zero
                && o.PostingTimeout.TotalMilliseconds <= int.MaxValue,
                "PostingTimeout must be positive and within HttpClient's supported range.")
            .ValidateOnStart();
        if (configure is not null) options.Configure(configure);

        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContextFactory<AppDbContext>((sp, db) =>
        {
            var settings = sp.GetRequiredService<IOptions<SocialPosterOptions>>().Value;
            var connection = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Path.GetFullPath(settings.DataDirectory), settings.DatabaseFileName),
            };
            db.UseSqlite(connection.ToString());
        });
        services.AddSingleton<SecretStore>();
        // The platform clients read credentials through ICredentialStore; here that is the encrypted local store.
        services.AddSingleton<ICredentialStore>(sp => sp.GetRequiredService<SecretStore>());
        services.AddSocialPosting();
        services.AddOptions<SocialPostingOptions>()
            .Configure<IOptions<SocialPosterOptions>>((posting, core) => posting.PostingTimeout = core.Value.PostingTimeout);
        services.AddScoped<ContactService>();
        services.AddScoped<PublishService>();
        services.AddSingleton<AgentSettingsStore>();
        services.AddSingleton<AccountFinder>();
        services.AddSingleton<AccountSearchService>();
        return services;
    }

    /// <summary>
    /// Creates the storage directory and dedicated SQLite schema, then applies legacy schema upgrades.
    /// Call on the built host before serving requests. Safe to call again sequentially.
    /// </summary>
    /// <param name="services">The built host's service provider.</param>
    /// <param name="cancellationToken">Cancels database operations.</param>
    /// <returns>A task completing when storage is ready.</returns>
    public static async Task InitializeSocialPosterAsync(
        this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using var scope = services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<IOptions<SocialPosterOptions>>().Value;
        var directory = Path.GetFullPath(settings.DataDirectory);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await db.UpgradeSchemaAsync(cancellationToken);
    }
}
