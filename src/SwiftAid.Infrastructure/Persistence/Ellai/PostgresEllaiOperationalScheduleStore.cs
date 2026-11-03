using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

public sealed record EllaiOperationalLease(string WorkspaceId, string RoleId, Guid LeaseId,
    long FencingToken, DateTimeOffset DueAtUtc, DateTimeOffset LeaseUntilUtc, string PolicyVersion);

/// <summary>Durable scoped schedule and lease coordination. This store does not start work
/// or grant a worker access to any case; the caller must supply trusted scope and authorization.</summary>
public sealed class PostgresEllaiOperationalScheduleStore(SwiftAidDbContext db)
{
    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
    {
        "ops.enrichment", "ops.quality", "ops.followup", "ops.organization", "ops.correlation", "ops.spatial"
    };

    public async Task<bool> CreateAsync(string workspaceId, string roleId, int cadenceHours,
        string policyVersion, DateTimeOffset firstDueUtc, CancellationToken ct)
    {
        ValidateScope(workspaceId, roleId);
        if (cadenceHours is not (4 or 8) || string.IsNullOrWhiteSpace(policyVersion) ||
            policyVersion.Length > 120 || firstDueUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("A bounded policy, UTC due time, and 4- or 8-hour cadence are required.");
        return await ExecuteAsync(async connection =>
        {
            await using var cmd = Command(connection, """
                INSERT INTO ellai_operational_schedules (workspace_id, role_id, cadence_hours, policy_version, next_due_utc)
                VALUES (@workspace, @role, @cadence, @policy, @due)
                ON CONFLICT (workspace_id, role_id) DO NOTHING
                """, ("workspace", workspaceId), ("role", roleId), ("cadence", cadenceHours),
                ("policy", policyVersion), ("due", firstDueUtc));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, ct).ConfigureAwait(false);
    }

    public async Task<EllaiOperationalLease?> TryClaimAsync(string workspaceId, string roleId,
        TimeSpan duration, CancellationToken ct)
    {
        ValidateScope(workspaceId, roleId);
        if (duration < TimeSpan.FromSeconds(10) || duration > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(duration));
        var leaseId = Guid.NewGuid();
        return await ExecuteAsync(async connection =>
        {
            await using var cmd = Command(connection, """
                UPDATE ellai_operational_schedules
                SET lease_id = @lease, lease_started_utc = clock_timestamp(), lease_until_utc = clock_timestamp() + @duration,
                    fencing_token = fencing_token + 1
                WHERE workspace_id = @workspace AND role_id = @role AND next_due_utc <= clock_timestamp()
                    AND (lease_until_utc IS NULL OR lease_until_utc <= clock_timestamp())
                RETURNING fencing_token, next_due_utc, lease_until_utc, policy_version
                """, ("lease", leaseId), ("duration", duration), ("workspace", workspaceId), ("role", roleId));
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            return new EllaiOperationalLease(workspaceId, roleId, leaseId, reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3));
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(EllaiOperationalLease lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateScope(lease.WorkspaceId, lease.RoleId);
        return await ExecuteAsync(async connection =>
        {
            await using var cmd = Command(connection, """
                UPDATE ellai_operational_schedules
                SET next_due_utc = next_due_utc + make_interval(secs =>
                    (cadence_hours * 3600 * (floor(greatest(0, extract(epoch FROM (clock_timestamp() - next_due_utc))) /
                    (cadence_hours * 3600)) + 1))::double precision),
                    lease_id = NULL, lease_started_utc = NULL, lease_until_utc = NULL
                WHERE workspace_id = @workspace AND role_id = @role AND lease_id = @lease
                    AND fencing_token = @fence AND policy_version = @policy AND lease_until_utc > clock_timestamp()
                """, ("workspace", lease.WorkspaceId), ("role", lease.RoleId), ("lease", lease.LeaseId),
                ("fence", lease.FencingToken), ("policy", lease.PolicyVersion));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> RenewAsync(EllaiOperationalLease lease, TimeSpan duration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateScope(lease.WorkspaceId, lease.RoleId);
        if (duration < TimeSpan.FromSeconds(10) || duration > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(duration));
        return await ExecuteAsync(async connection =>
        {
            await using var cmd = Command(connection, """
                UPDATE ellai_operational_schedules SET lease_until_utc = least(clock_timestamp() + @duration,
                    lease_started_utc + interval '10 minutes')
                WHERE workspace_id = @workspace AND role_id = @role AND lease_id = @lease
                    AND fencing_token = @fence AND policy_version = @policy AND lease_until_utc > clock_timestamp()
                    AND lease_started_utc + interval '10 minutes' > clock_timestamp()
                """, ("duration", duration), ("workspace", lease.WorkspaceId), ("role", lease.RoleId),
                ("lease", lease.LeaseId), ("fence", lease.FencingToken), ("policy", lease.PolicyVersion));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Leave the due cursor unchanged after failed or cancelled work.</summary>
    public async Task<bool> AbandonAsync(EllaiOperationalLease lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateScope(lease.WorkspaceId, lease.RoleId);
        return await ExecuteAsync(async connection =>
        {
            await using var cmd = Command(connection, """
                UPDATE ellai_operational_schedules SET lease_id = NULL, lease_started_utc = NULL, lease_until_utc = NULL
                WHERE workspace_id = @workspace AND role_id = @role AND lease_id = @lease
                    AND fencing_token = @fence AND policy_version = @policy AND lease_until_utc > clock_timestamp()
                """, ("workspace", lease.WorkspaceId), ("role", lease.RoleId), ("lease", lease.LeaseId),
                ("fence", lease.FencingToken), ("policy", lease.PolicyVersion));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, ct).ConfigureAwait(false);
    }

    private static void ValidateScope(string workspaceId, string roleId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || workspaceId.Length > 120 ||
            !string.Equals(workspaceId, workspaceId.Trim(), StringComparison.Ordinal) || !Roles.Contains(roleId))
            throw new ArgumentException("A known role and bounded workspace are required.");
    }

    private async Task<T> ExecuteAsync<T>(Func<DbConnection, Task<T>> action, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try { return await action(connection).ConfigureAwait(false); }
        finally { if (openedHere) await db.Database.CloseConnectionAsync().ConfigureAwait(false); }
    }

    private static DbCommand Command(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }
}
