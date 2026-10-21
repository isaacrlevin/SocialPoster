using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SocialPoster.Core.Data;

namespace SocialPoster.Core.Scheduling;

/// <summary>SQLite aggregate storage, using conditional single-statement updates for claims and fencing.</summary>
public sealed class SqliteScheduledPostStore(IDbContextFactory<AppDbContext> factory) : IScheduledPostStore
{
    internal const string Schema = """
        CREATE TABLE IF NOT EXISTS "ScheduledPosts" (
            "Id" TEXT NOT NULL PRIMARY KEY,
            "Revision" INTEGER NOT NULL,
            "State" INTEGER NOT NULL,
            "NextAttempt" INTEGER NOT NULL,
            "LeaseToken" TEXT NULL,
            "LeaseUntil" INTEGER NULL,
            "Json" TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_ScheduledPosts_Due" ON "ScheduledPosts" ("State", "NextAttempt");
        """;

    public async Task InsertAsync(ScheduledPost post, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ScheduledPosts" ("Id","Revision","State","NextAttempt","LeaseToken","LeaseUntil","Json")
            VALUES ({post.Id},{post.Revision},{(int)post.State},{post.NextAttemptUtc.ToUnixTimeMilliseconds()},
                {post.LeaseToken},{(post.LeaseUntilUtc.HasValue ? post.LeaseUntilUtc.Value.ToUnixTimeMilliseconds() : (long?)null)},
                {JsonSerializer.Serialize(post)})
            """, ct);
    }

    public async Task<ScheduledPost?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var json = await db.Database.SqlQuery<string>($"SELECT \"Json\" AS \"Value\" FROM \"ScheduledPosts\" WHERE \"Id\" = {id}").FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<ScheduledPost>(json)!;
    }

    public async Task<IReadOnlyList<ScheduledPost>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQueryRaw<string>("SELECT \"Json\" AS \"Value\" FROM \"ScheduledPosts\" ORDER BY \"NextAttempt\" DESC").ToListAsync(ct);
        return rows.Select(j => JsonSerializer.Deserialize<ScheduledPost>(j)!).ToList();
    }

    public async Task<ScheduledPost?> ClaimAsync(DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ticks = now.ToUnixTimeMilliseconds();
        var rows = await db.Database.SqlQuery<string>($"""
            SELECT "Json" AS "Value" FROM "ScheduledPosts"
            WHERE ("State" = {(int)ScheduleState.Queued} AND "NextAttempt" <= {ticks})
               OR ("State" = {(int)ScheduleState.Running} AND "LeaseUntil" <= {ticks})
            ORDER BY "NextAttempt" LIMIT 16
            """).ToListAsync(ct);
        foreach (var json in rows)
        {
            var old = JsonSerializer.Deserialize<ScheduledPost>(json)!;
            var claimed = old with
            {
                Revision = old.Revision + 1, State = ScheduleState.Running,
                LeaseToken = Guid.NewGuid().ToString("N"), LeaseUntilUtc = now + leaseDuration,
                Outcomes = old.Outcomes.Select(o => o.State == DeliveryState.InFlight
                    ? o with { State = DeliveryState.Uncertain, Detail = "Worker lease expired during delivery; check the platform before posting again." } : o).ToArray(),
            };
            var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "ScheduledPosts" SET "Revision"={claimed.Revision}, "State"={(int)claimed.State},
                    "LeaseToken"={claimed.LeaseToken}, "LeaseUntil"={claimed.LeaseUntilUtc.Value.ToUnixTimeMilliseconds()},
                    "Json"={JsonSerializer.Serialize(claimed)}
                WHERE "Id"={old.Id} AND "Revision"={old.Revision}
                  AND (("State"={(int)ScheduleState.Queued} AND "NextAttempt"<={ticks})
                    OR ("State"={(int)ScheduleState.Running} AND "LeaseUntil"<={ticks}))
                """, ct);
            if (count == 1) return claimed;
        }
        return null;
    }

    public async Task<bool> ReplaceAsync(ScheduledPost post, long revision, string? owner, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        post = post with { Revision = revision + 1 };
        var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ScheduledPosts" SET "Revision"={post.Revision}, "State"={(int)post.State},
                "NextAttempt"={post.NextAttemptUtc.ToUnixTimeMilliseconds()}, "LeaseToken"={post.LeaseToken},
                "LeaseUntil"={(post.LeaseUntilUtc.HasValue ? post.LeaseUntilUtc.Value.ToUnixTimeMilliseconds() : (long?)null)},
                "Json"={JsonSerializer.Serialize(post)}
            WHERE "Id"={post.Id} AND "Revision"={revision}
              AND (({owner} IS NULL AND "LeaseToken" IS NULL AND "State"={(int)ScheduleState.Queued})
                OR ({owner} IS NOT NULL AND "LeaseToken"={owner} AND "State"={(int)ScheduleState.Running}
                    AND "LeaseUntil">{now.ToUnixTimeMilliseconds()}))
            """, ct);
        return count == 1;
    }
}
