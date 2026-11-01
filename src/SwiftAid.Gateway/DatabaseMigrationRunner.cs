using Microsoft.EntityFrameworkCore;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Gateway;

/// <summary>One-shot deployment mode; never starts HTTP listeners or application workers.</summary>
public static class DatabaseMigrationRunner
{
    public static async Task<int> RunAsync(string connectionString, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var options = new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql(connectionString, postgres => postgres.CommandTimeout(300))
            .UseOpenIddict()
            .Options;
        await using var db = new SwiftAidDbContext(options);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var ct = deadline.Token;
        await db.Database.OpenConnectionAsync(ct);
        var locked = false;
        try
        {
            // A session lock covers discovery and all EF migration transactions, including no-op runs.
            while (!locked)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT pg_try_advisory_lock(734894581)";
                locked = (bool)(await command.ExecuteScalarAsync(ct))!;
                if (!locked) await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }

            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
            await db.Database.MigrateAsync(ct);
            return pending;
        }
        finally
        {
            if (locked)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(734894581)";
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
            await db.Database.CloseConnectionAsync();
        }
    }
}
