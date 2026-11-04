using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>Reads one current designation only under the same scoped administrator authority used for changes.</summary>
public sealed class PostgresHealthOfficerDesignationQuery(SwiftAidDbContext db, HealthOfficerDirectoryOptions options)
    : IHealthOfficerDesignationQuery
{
    public async Task<HealthOfficerDesignationReadResult> GetAsync(ActorContext actor, HealthOfficerDesignationScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        if (!options.Enabled || !options.AdministrationEnabled) return new(HealthOfficerDesignationReadStatus.Unavailable);
        if (!PostgresOfficerDesignationAuthority.Identifier(scope.TargetActorId) ||
            !PostgresOfficerDesignationAuthority.ValidScope(scope.OrganizationId, scope.CountryCode, scope.Sector))
            return new(HealthOfficerDesignationReadStatus.Invalid);

        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (!await PostgresOfficerDesignationAuthority.AcquireAsync(db, actor, scope.TargetActorId,
                scope.OrganizationId, scope.CountryCode, scope.Sector, ct).ConfigureAwait(false))
            return new(HealthOfficerDesignationReadStatus.Denied);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(hashtextextended('officer-designation:' ||
                jsonb_build_array({scope.TargetActorId}, {scope.OrganizationId}, {scope.CountryCode}, {scope.Sector})::text, 0))
            """, ct).ConfigureAwait(false);
        var rows = await db.Database.SqlQuery<DesignationRow>($"""
            SELECT designation_id AS "DesignationId", version AS "Version", status AS "Status",
                valid_from_utc AS "ValidFromUtc", expires_at_utc AS "ExpiresAtUtc"
            FROM health_officer_designations WHERE actor_id = {scope.TargetActorId} AND organization_id = {scope.OrganizationId}
                AND country_code = {scope.CountryCode} AND sector = {scope.Sector}
            ORDER BY version DESC LIMIT 1
            """).ToArrayAsync(ct).ConfigureAwait(false);
        var row = rows.SingleOrDefault();
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(HealthOfficerDesignationReadStatus.Available, row is null ? null :
            new(row.DesignationId, row.Version, row.Status, row.ValidFromUtc, row.ExpiresAtUtc));
    }

    private sealed class DesignationRow
    {
        public Guid DesignationId { get; init; }
        public long Version { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset ValidFromUtc { get; init; }
        public DateTimeOffset ExpiresAtUtc { get; init; }
    }
}
