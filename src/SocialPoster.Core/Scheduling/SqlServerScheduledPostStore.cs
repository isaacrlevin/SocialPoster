using System.Data;
using System.Data.Common;
using System.Text.Json;

namespace SocialPoster.Core.Scheduling;

/// <summary>
/// Shared-store adapter for SQL Server/Azure SQL. The host supplies fresh closed provider connections
/// (for example Microsoft.Data.SqlClient); this library adds no SQL Server provider dependency.
/// Schema initialization is an explicit deployment/startup operation, not a poll-time operation.
/// </summary>
public sealed class SqlServerScheduledPostStore(Func<DbConnection> connectionFactory) : IScheduledPostStore
{
    /// <summary>Creates only the scheduling table. Use a deployment identity with DDL permission once before starting workers.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'dbo.SocialPosterSchedules', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.SocialPosterSchedules (
                    Id varchar(32) NOT NULL PRIMARY KEY,
                    Revision bigint NOT NULL,
                    State int NOT NULL,
                    NextAttempt bigint NOT NULL,
                    LeaseToken varchar(32) NULL,
                    LeaseUntil bigint NULL,
                    Json nvarchar(max) NOT NULL
                );
                CREATE INDEX IX_SocialPosterSchedules_Due ON dbo.SocialPosterSchedules(State, NextAttempt);
            END
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertAsync(ScheduledPost post, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.SocialPosterSchedules (Id, Revision, State, NextAttempt, LeaseToken, LeaseUntil, Json)
            VALUES (@id, @revision, @state, @next, @token, @until, @json)
            """;
        AddPost(command, post);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ScheduledPost?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM dbo.SocialPosterSchedules WHERE Id = @id";
        Add(command, "@id", id, DbType.AnsiString);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return json is null ? null : JsonSerializer.Deserialize<ScheduledPost>(json)!;
    }

    public async Task<IReadOnlyList<ScheduledPost>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM dbo.SocialPosterSchedules ORDER BY NextAttempt DESC";
        return await ReadAsync(command, ct);
    }

    public async Task<ScheduledPost?> ClaimAsync(DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT TOP (16) Json FROM dbo.SocialPosterSchedules
            WHERE (State = 0 AND NextAttempt <= @now) OR (State = 1 AND LeaseUntil <= @now)
            ORDER BY NextAttempt
            """;
        Add(select, "@now", now.ToUnixTimeMilliseconds(), DbType.Int64);
        var candidates = await ReadAsync(select, ct);
        foreach (var old in candidates)
        {
            var claimed = old with
            {
                Revision = old.Revision + 1, State = ScheduleState.Running,
                LeaseToken = Guid.NewGuid().ToString("N"), LeaseUntilUtc = now + leaseDuration,
                Outcomes = old.Outcomes.Select(o => o.State == DeliveryState.InFlight
                    ? o with { State = DeliveryState.Uncertain, Detail = "Worker lease expired during delivery; check the platform before posting again." } : o).ToArray(),
            };
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE dbo.SocialPosterSchedules SET Revision=@revision, State=@state, LeaseToken=@token, LeaseUntil=@until, Json=@json
                WHERE Id=@id AND Revision=@oldRevision
                  AND ((State=0 AND NextAttempt<=@now) OR (State=1 AND LeaseUntil<=@now))
                """;
            AddPost(update, claimed);
            Add(update, "@oldRevision", old.Revision, DbType.Int64);
            Add(update, "@now", now.ToUnixTimeMilliseconds(), DbType.Int64);
            if (await update.ExecuteNonQueryAsync(ct) == 1) return claimed;
        }
        return null;
    }

    public async Task<bool> ReplaceAsync(ScheduledPost post, long revision, string? owner, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.SocialPosterSchedules SET Revision=@revision, State=@state, NextAttempt=@next,
                LeaseToken=@token, LeaseUntil=@until, Json=@json
            WHERE Id=@id AND Revision=@oldRevision
              AND ((@owner IS NULL AND LeaseToken IS NULL AND State=0)
                OR (@owner IS NOT NULL AND LeaseToken=@owner AND State=1 AND LeaseUntil>@now))
            """;
        AddPost(command, post with { Revision = revision + 1 });
        Add(command, "@oldRevision", revision, DbType.Int64);
        Add(command, "@owner", owner, DbType.AnsiString);
        Add(command, "@now", now.ToUnixTimeMilliseconds(), DbType.Int64);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = connectionFactory();
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch (Exception ex) when (ex is DbException or OperationCanceledException or InvalidOperationException)
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static void AddPost(DbCommand command, ScheduledPost post)
    {
        Add(command, "@id", post.Id, DbType.AnsiString);
        Add(command, "@revision", post.Revision, DbType.Int64);
        Add(command, "@state", (int)post.State, DbType.Int32);
        Add(command, "@next", post.NextAttemptUtc.ToUnixTimeMilliseconds(), DbType.Int64);
        Add(command, "@token", post.LeaseToken, DbType.AnsiString);
        Add(command, "@until", post.LeaseUntilUtc?.ToUnixTimeMilliseconds(), DbType.Int64);
        Add(command, "@json", JsonSerializer.Serialize(post), DbType.String);
    }

    private static void Add(DbCommand command, string name, object? value, DbType type)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        // Prevent inferred nvarchar(4000) truncation and null varchar(1) lease tokens with SqlClient.
        if (type == DbType.String) parameter.Size = -1;
        else if (type == DbType.AnsiString) parameter.Size = 32;
        command.Parameters.Add(parameter);
    }

    private static async Task<List<ScheduledPost>> ReadAsync(DbCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<ScheduledPost>();
        while (await reader.ReadAsync(ct)) results.Add(JsonSerializer.Deserialize<ScheduledPost>(reader.GetString(0))!);
        return results;
    }
}
