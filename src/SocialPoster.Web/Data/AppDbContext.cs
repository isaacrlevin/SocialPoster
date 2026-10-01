using Microsoft.EntityFrameworkCore;
using SocialPoster.Core;

namespace SocialPoster.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<PostHistoryEntry> PostHistory => Set<PostHistoryEntry>();
    public DbSet<AccountSearch> AccountSearches => Set<AccountSearch>();

    /// <summary>Creates tables added after the first release. The schema is created with EnsureCreated, which doesn't upgrade existing databases.</summary>
    public async Task UpgradeSchemaAsync(CancellationToken ct = default)
    {
        await Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "AccountSearches" (
                "Key" TEXT NOT NULL CONSTRAINT "PK_AccountSearches" PRIMARY KEY,
                "ContactId" INTEGER NULL,
                "Query" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "Error" TEXT NULL,
                "CandidatesJson" TEXT NOT NULL,
                "Model" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                "CompletedAtUtc" TEXT NULL
            );
            """, ct);

        await AddColumnIfMissingAsync("Contacts", "XVerifiedByCopilotAtUtc", "TEXT", ct);
        await AddColumnIfMissingAsync("Contacts", "LinkedInVerifiedByCopilotAtUtc", "TEXT", ct);
    }

    private async Task AddColumnIfMissingAsync(string table, string column, string sqlType, CancellationToken ct)
    {
        await Database.OpenConnectionAsync(ct);
        try
        {
            using var check = Database.GetDbConnection().CreateCommand();
            check.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using var reader = await check.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(reader["name"]?.ToString(), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }
        finally
        {
            await Database.CloseConnectionAsync();
        }

        await Database.ExecuteSqlRawAsync(string.Concat("ALTER TABLE \"", table, "\" ADD COLUMN \"", column, "\" ", sqlType), ct);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Contact>(e =>
        {
            e.HasIndex(c => c.Key).IsUnique();
            e.HasIndex(c => c.BlueskyDid);
            e.HasIndex(c => c.XUsername);
            e.Property(c => c.Key).UseCollation("NOCASE");
            e.Property(c => c.XUsername).UseCollation("NOCASE");
            e.Property(c => c.BlueskyHandle).UseCollation("NOCASE");
            e.Property(c => c.Source).HasConversion<string>();
        });

        b.Entity<Setting>().HasKey(s => s.Key);

        b.Entity<AccountSearch>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Status).HasConversion<string>();
        });
    }
}

public enum AccountSearchStatus
{
    Running,
    Done,
    Failed,
}

/// <summary>Cached result of an AI account search. Key is <c>contact:{id}</c>.</summary>
public class AccountSearch
{
    public string Key { get; set; } = "";
    public int? ContactId { get; set; }
    public string Query { get; set; } = "";
    public AccountSearchStatus Status { get; set; }
    public string? Error { get; set; }

    /// <summary>JSON array of <see cref="AccountCandidate"/>.</summary>
    public string CandidatesJson { get; set; } = "[]";
    public string? Model { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    public static string KeyFor(int contactId) => $"contact:{contactId}";
}

/// <summary>Key/value setting; values are encrypted with ASP.NET Core Data Protection.</summary>
public class Setting
{
    public string Key { get; set; } = "";
    public string ProtectedValue { get; set; } = "";
}

public class PostHistoryEntry
{
    public int Id { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Template { get; set; } = "";

    /// <summary>JSON array of <see cref="Services.PublishResult"/>.</summary>
    public string ResultsJson { get; set; } = "[]";
}
