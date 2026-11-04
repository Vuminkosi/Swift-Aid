using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Integration.Tests;

[Collection("Ellai framework PostgreSQL worker")]
public sealed class EllaiOperationalSchedulePostgresTests
{
    [FrameworkPostgresFact]
    public async Task FencedWorkCommitsChangesOnlyWhileLeaseIsCurrent()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        var store = new PostgresEllaiOperationalScheduleStore(db);
        const string role = "ops.quality";
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE ellai_fenced_test (value integer NOT NULL)");
        try
        {
            Assert.True(await store.CreateAsync(fixture.WorkspaceId, role, 4, "policy/1", DateTimeOffset.UtcNow.AddMinutes(-1), default));
            var lease = await store.TryClaimAsync(fixture.WorkspaceId, role, TimeSpan.FromMinutes(1), default);
            Assert.NotNull(lease);
            async Task<bool> Write(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction tx,
                CancellationToken ct, bool changed, bool delay = false)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = delay
                    ? "INSERT INTO ellai_fenced_test VALUES (2); SELECT pg_sleep(0.5)"
                    : "INSERT INTO ellai_fenced_test VALUES (1)";
                await command.ExecuteNonQueryAsync(ct);
                return changed;
            }
            var noChange = await store.ExecuteFencedAsync(lease, (c, t, ct) => Write(c, t, ct, false), default);
            Assert.Equal(EllaiFencedWorkOutcome.NoChange, noChange);
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM ellai_fenced_test").SingleAsync());
            var committed = await store.ExecuteFencedAsync(lease, (c, t, ct) => Write(c, t, ct, true), default);
            Assert.Equal(EllaiFencedWorkOutcome.Committed, committed);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ellai_operational_schedules SET lease_until_utc = clock_timestamp() + interval '0.2 seconds' WHERE workspace_id = {fixture.WorkspaceId} AND role_id = {role}");
            var expired = await store.ExecuteFencedAsync(lease, (c, t, ct) => Write(c, t, ct, true, true), default);
            Assert.Equal(EllaiFencedWorkOutcome.StaleLease, expired);
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM ellai_fenced_test").SingleAsync());
            var called = false;
            var rejected = await store.ExecuteFencedAsync(lease, (_, _, _) => { called = true; return Task.FromResult(true); }, default);
            Assert.Equal(EllaiFencedWorkOutcome.StaleLease, rejected);
            Assert.False(called);
            var replacement = await store.TryClaimAsync(fixture.WorkspaceId, role, TimeSpan.FromMinutes(1), default);
            Assert.NotNull(replacement);
            var oldFence = await store.ExecuteFencedAsync(lease, (_, _, _) => { called = true; return Task.FromResult(true); }, default);
            Assert.Equal(EllaiFencedWorkOutcome.StaleLease, oldFence);
            Assert.False(called);
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ExecuteFencedAsync(replacement,
                async (c, t, ct) => { await Write(c, t, ct, true); cancellation.Cancel(); return true; }, cancellation.Token));
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM ellai_fenced_test").SingleAsync());
        }
        finally
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM ellai_operational_schedules WHERE workspace_id = {fixture.WorkspaceId} AND role_id = {role}");
        }
    }

    [FrameworkPostgresFact]
    public async Task DueDiscoveryClaimsOnlyPermittedWorkspaceAndRolesAcrossReplicas()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();
        var first = new PostgresEllaiOperationalScheduleStore(firstDb);
        var second = new PostgresEllaiOperationalScheduleStore(secondDb);
        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        try
        {
            Assert.True(await first.CreateAsync(fixture.WorkspaceId, "ops.quality", 4, "policy/1", due, default));
            Assert.True(await first.CreateAsync(fixture.WorkspaceId, "ops.followup", 4, "policy/1", due, default));
            Assert.True(await first.CreateAsync(fixture.AlternateWorkspaceId, "ops.quality", 4, "policy/1", due, default));
            Assert.Null(await first.TryClaimNextAsync(fixture.WorkspaceId, [], TimeSpan.FromMinutes(1), default));
            await Assert.ThrowsAsync<ArgumentException>(() => first.TryClaimNextAsync(fixture.WorkspaceId,
                ["ops.unknown"], TimeSpan.FromMinutes(1), default));
            var permitted = new[] { "ops.quality", "ops.followup" };
            var claims = await Task.WhenAll(first.TryClaimNextAsync(fixture.WorkspaceId, permitted, TimeSpan.FromMinutes(1), default),
                second.TryClaimNextAsync(fixture.WorkspaceId, permitted, TimeSpan.FromMinutes(1), default));
            Assert.All(claims, lease => Assert.NotNull(lease));
            Assert.All(claims, lease => Assert.Equal(fixture.WorkspaceId, lease!.WorkspaceId));
            Assert.Equal(2, claims.Select(lease => lease!.RoleId).Distinct(StringComparer.Ordinal).Count());
            Assert.Null(await first.TryClaimNextAsync(fixture.WorkspaceId, permitted, TimeSpan.FromMinutes(1), default));
            Assert.Null(await first.TryClaimNextAsync(fixture.AlternateWorkspaceId, ["ops.followup"], TimeSpan.FromMinutes(1), default));
            Assert.NotNull(await first.TryClaimNextAsync(fixture.AlternateWorkspaceId, ["ops.quality"], TimeSpan.FromMinutes(1), default));
        }
        finally
        {
            await firstDb.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM ellai_operational_schedules WHERE workspace_id = {fixture.WorkspaceId} OR workspace_id = {fixture.AlternateWorkspaceId}");
        }
    }

    [FrameworkPostgresFact]
    public async Task ScheduleClaimsAreScopedFencedAndAdvanceWithoutCatchupStorm()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();
        var first = new PostgresEllaiOperationalScheduleStore(firstDb);
        var second = new PostgresEllaiOperationalScheduleStore(secondDb);
        const string role = "ops.quality";
        try
        {
            Assert.True(await first.CreateAsync(fixture.WorkspaceId, role, 4, "policy/1",
                DateTimeOffset.UtcNow.AddHours(-9), default));
            Assert.False(await second.CreateAsync(fixture.WorkspaceId, role, 8, "policy/2",
                DateTimeOffset.UtcNow, default));
            var claims = await Task.WhenAll(first.TryClaimAsync(fixture.WorkspaceId, role, TimeSpan.FromMinutes(1), default),
                second.TryClaimAsync(fixture.WorkspaceId, role, TimeSpan.FromMinutes(1), default));
            var original = Assert.Single(claims, item => item is not null)!;
            Assert.Null(await first.TryClaimAsync(fixture.AlternateWorkspaceId, role, TimeSpan.FromMinutes(1), default));
            await firstDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE ellai_operational_schedules SET lease_until_utc = clock_timestamp() - interval '1 second' WHERE workspace_id = {fixture.WorkspaceId} AND role_id = {role}");
            Assert.False(await first.CompleteAsync(original, default));
            var replacement = await second.TryClaimAsync(fixture.WorkspaceId, role, TimeSpan.FromMinutes(1), default);
            Assert.NotNull(replacement);
            Assert.True(replacement.FencingToken > original.FencingToken);
            Assert.False(await first.CompleteAsync(original, default));
            Assert.False(await first.RenewAsync(original, TimeSpan.FromMinutes(1), default));
            Assert.False(await first.AbandonAsync(original, default));
            Assert.True(await second.RenewAsync(replacement, TimeSpan.FromMinutes(1), default));
            await firstDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE ellai_operational_schedules SET lease_started_utc = clock_timestamp() - interval '11 minutes' WHERE workspace_id = {fixture.WorkspaceId} AND role_id = {role}");
            Assert.False(await second.RenewAsync(replacement, TimeSpan.FromMinutes(1), default));
            Assert.True(await second.AbandonAsync(replacement, default));
            var resumed = await first.TryClaimAsync(fixture.WorkspaceId, role, TimeSpan.FromMinutes(1), default);
            Assert.NotNull(resumed);
            Assert.True(resumed.FencingToken > replacement.FencingToken);
            Assert.True(await first.CompleteAsync(resumed, default));
            Assert.False(await second.CompleteAsync(replacement, default));
            Assert.Null(await first.TryClaimAsync(fixture.WorkspaceId, role, TimeSpan.FromMinutes(1), default));
        }
        finally
        {
            await firstDb.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM ellai_operational_schedules WHERE workspace_id = {fixture.WorkspaceId} AND role_id = {role}");
        }
    }

    [FrameworkPostgresFact]
    public async Task EightHourScheduleSkipsMissedIntervalsAndAdvancesFromOriginalDueTime()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        var store = new PostgresEllaiOperationalScheduleStore(db);
        const string role = "ops.spatial";
        var due = DateTimeOffset.UtcNow.AddHours(-17);
        try
        {
            Assert.True(await store.CreateAsync(fixture.WorkspaceId, role, 8, "policy/1", due, default));
            var lease = await store.TryClaimAsync(fixture.WorkspaceId, role, TimeSpan.FromMinutes(1), default);
            Assert.NotNull(lease);
            Assert.True(await store.CompleteAsync(lease, default));
            await db.Database.OpenConnectionAsync();
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT next_due_utc FROM ellai_operational_schedules WHERE workspace_id = @workspace AND role_id = @role";
            var workspaceParameter = cmd.CreateParameter();
            workspaceParameter.ParameterName = "workspace";
            workspaceParameter.Value = fixture.WorkspaceId;
            cmd.Parameters.Add(workspaceParameter);
            var roleParameter = cmd.CreateParameter();
            roleParameter.ParameterName = "role";
            roleParameter.Value = role;
            cmd.Parameters.Add(roleParameter);
            var nextDue = (DateTime)(await cmd.ExecuteScalarAsync())!;
            Assert.InRange((nextDue - due.UtcDateTime.AddHours(24)).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
            Assert.Null(await store.TryClaimAsync(fixture.WorkspaceId, role, TimeSpan.FromMinutes(1), default));
        }
        finally
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM ellai_operational_schedules WHERE workspace_id = {fixture.WorkspaceId} AND role_id = {role}");
        }
    }

    [FrameworkPostgresFact]
    public async Task MigrationRollbackRefusesToDiscardScheduleState()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        var store = new PostgresEllaiOperationalScheduleStore(db);
        const string role = "ops.followup";
        var migrator = db.GetService<IMigrator>();
        try
        {
            Assert.True(await store.CreateAsync(fixture.WorkspaceId, role, 4, "policy/1", DateTimeOffset.UtcNow, default));
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync("20261103200000_EllaiFrameworkSession"));
            Assert.NotNull(await store.TryClaimAsync(fixture.WorkspaceId, role, TimeSpan.FromMinutes(1), default));
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM ellai_operational_schedules WHERE workspace_id = {fixture.WorkspaceId} AND role_id = {role}");
            await migrator.MigrateAsync("20261103200000_EllaiFrameworkSession");
            await migrator.MigrateAsync();
        }
        finally
        {
            await migrator.MigrateAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM ellai_operational_schedules WHERE workspace_id = {fixture.WorkspaceId} AND role_id = {role}");
        }
    }
}
