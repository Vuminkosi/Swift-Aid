using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftAid.Contracts.Events;

namespace SwiftAid.Integration.Tests;

[Trait("Category", "IntakePostgres")]
[Collection(IntakePostgresCollection.Name)]
public sealed class CaseEventTypeConstraintPostgresTests
{
    [IntakePostgresFact]
    public async Task Migrated_constraint_matches_the_event_contract_and_rejects_unknown_types()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pg_get_constraintdef(constraint_row.oid)
            FROM pg_constraint AS constraint_row
            JOIN pg_class AS table_row ON table_row.oid = constraint_row.conrelid
            WHERE table_row.relname = 'case_events'
              AND constraint_row.conname = 'ck_case_events_type';
            """;
        var definition = (string?)await command.ExecuteScalarAsync();
        Assert.NotNull(definition);

        var eventTypes = Enum.GetNames<CaseEventType>();
        Assert.Equal(24, eventTypes.Length);
        Assert.All(eventTypes, eventType => Assert.Contains($"'{eventType}'", definition, StringComparison.Ordinal));

        var rejected = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO case_events
                (event_id, case_id, aggregate_version, report_revision, command_id, actor_id,
                 event_type, schema_version, occurred_at_utc, recorded_at_utc, payload_id)
            VALUES
                ({Guid.NewGuid()}, {Guid.NewGuid()}, 1, 1, {Guid.NewGuid()}, 'migration-test',
                 'UnsupportedCaseEvent', 1, NULL, {DateTimeOffset.UtcNow}, {Guid.NewGuid()});
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, rejected.SqlState);
        Assert.Equal("ck_case_events_type", rejected.ConstraintName);
    }
}
