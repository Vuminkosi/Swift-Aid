using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Integration.Tests;

[Collection("Ellai framework PostgreSQL worker")]
public sealed class EllaiOperationalSchedulePostgresTests
{
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
