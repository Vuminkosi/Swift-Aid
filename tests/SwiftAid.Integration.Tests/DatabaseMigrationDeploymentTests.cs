using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SwiftAid.Gateway;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class DatabaseMigrationDeploymentTests
{
    [IntakePostgresFact]
    public async Task Deployment_upgrades_an_existing_database_and_serializes_repeated_runs()
    {
        var original = Environment.GetEnvironmentVariable("SWIFTAID_TEST_INTAKE_POSTGRES_CONNECTION")!;
        var database = "migration_deploy_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(original);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin))
            await create.ExecuteNonQueryAsync();
        var connection = new NpgsqlConnectionStringBuilder(original) { Database = database, Pooling = false }.ConnectionString;
        try
        {
            var options = new DbContextOptionsBuilder<SwiftAidDbContext>().UseNpgsql(connection).UseOpenIddict().Options;
            await using (var baseline = new SwiftAidDbContext(options))
                await baseline.GetService<IMigrator>().MigrateAsync("20261031213547_EllaiDurableConversationOutcomes");

            var results = await Task.WhenAll(DatabaseMigrationRunner.RunAsync(connection, default),
                DatabaseMigrationRunner.RunAsync(connection, default));
            Assert.Contains(0, results);
            Assert.True(results.Sum() > 0);
            Assert.Equal(0, await DatabaseMigrationRunner.RunAsync(connection, default));
            await using var verified = new SwiftAidDbContext(options);
            Assert.Empty(await verified.Database.GetPendingMigrationsAsync());
            Assert.True(await verified.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname='swiftaid_internal' AND p.proname='lock_case_authority' AND NOT p.prosecdef) AS "Value"
                """).SingleAsync());
            await verified.Database.ExecuteSqlRawAsync("SELECT 1 FROM swiftaid_classification.results LIMIT 1");

            // The topic concurrency mapping uses PostgreSQL's system xmin
            // column. Rolling its metadata migration down must not drop it.
            await verified.GetService<IMigrator>().MigrateAsync("20261103200000_EllaiEstimatedUsage");
            await verified.Database.ExecuteSqlRawAsync("SELECT xmin FROM ellai_topics LIMIT 0");
            await verified.GetService<IMigrator>().MigrateAsync();
            Assert.Empty(await verified.Database.GetPendingMigrationsAsync());

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseMigrationRunner.RunAsync(connection, cancelled.Token));
        }
        finally
        {
            // The unique test-created database is the only cleanup target.
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database}", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
