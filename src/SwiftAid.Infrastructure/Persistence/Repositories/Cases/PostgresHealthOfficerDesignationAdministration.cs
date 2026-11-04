using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>Only explicitly scoped administrators may append an officer designation; no authority is derived from case access.</summary>
public sealed class PostgresHealthOfficerDesignationAdministration(SwiftAidDbContext db, HealthOfficerDirectoryOptions options)
    : IHealthOfficerDesignationAdministration
{
    public async Task<HealthOfficerDesignationChangeResult> ChangeAsync(ActorContext actor, HealthOfficerDesignationChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(change);
        if (!options.Enabled || !options.AdministrationEnabled) return new(HealthOfficerDesignationChangeStatus.Unavailable);
        if (change.CommandId == Guid.Empty || !PostgresOfficerDesignationAuthority.Identifier(change.TargetActorId) ||
            !PostgresOfficerDesignationAuthority.ValidScope(change.OrganizationId, change.CountryCode, change.Sector) ||
            change.TargetActorId == actor.ActorId ||
            change.Status is not ("Active" or "Suspended" or "Revoked") || change.ExpectedVersion < 0 ||
            change.ExpectedVersion == long.MaxValue || change.ExpiresAtUtc <= change.ValidFromUtc)
            return new(HealthOfficerDesignationChangeStatus.Invalid);

        var from = Microseconds(change.ValidFromUtc);
        var expires = Microseconds(change.ExpiresAtUtc);
        if (expires <= from) return new(HealthOfficerDesignationChangeStatus.Invalid);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Stable lock ordering: command identity, administrator scope, shared authority rows, target scope.
        var commandLock = "officer-command:" + change.CommandId.ToString("D");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({commandLock}, 0))", ct).ConfigureAwait(false);
        var authorized = await PostgresOfficerDesignationAuthority.AcquireAsync(db, actor, change.TargetActorId,
            change.OrganizationId, change.CountryCode, change.Sector, ct).ConfigureAwait(false);
        if (!authorized) return new(HealthOfficerDesignationChangeStatus.Denied);

        var receipts = await db.Database.SqlQuery<Receipt>($"""
            SELECT administrator_actor_id AS "Administrator", target_actor_id AS "Target", organization_id AS "Organization",
                country_code AS "Country", sector AS "Sector", status AS "Status", valid_from_utc AS "ValidFrom",
                expires_at_utc AS "ExpiresAt", expected_version AS "ExpectedVersion", designation_id AS "DesignationId",
                designation_version AS "Version" FROM health_officer_designation_commands WHERE command_id = {change.CommandId}
            """).ToArrayAsync(ct).ConfigureAwait(false);
        if (receipts.SingleOrDefault() is { } receipt)
        {
            var same = receipt.Administrator == actor.ActorId && receipt.Target == change.TargetActorId && receipt.Organization == change.OrganizationId &&
                receipt.Country == change.CountryCode && receipt.Sector == change.Sector && receipt.Status == change.Status &&
                receipt.ValidFrom == from && receipt.ExpiresAt == expires && receipt.ExpectedVersion == change.ExpectedVersion;
            return same ? new(HealthOfficerDesignationChangeStatus.Replayed, receipt.DesignationId, receipt.Version) : new(HealthOfficerDesignationChangeStatus.Conflict);
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(hashtextextended('officer-designation:' ||
                jsonb_build_array({change.TargetActorId}, {change.OrganizationId}, {change.CountryCode}, {change.Sector})::text, 0))
            """, ct).ConfigureAwait(false);
        var targetValid = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM actors a JOIN organization_memberships m ON m.actor_id = a.actor_id
                WHERE a.actor_id = {change.TargetActorId} AND m.organization_id = {change.OrganizationId}
                    AND a.status = 'Active' AND (a.expires_at_utc IS NULL OR a.expires_at_utc > clock_timestamp())
                    AND m.status = 'Active' AND m.valid_from_utc <= clock_timestamp()
                    AND (m.expires_at_utc IS NULL OR m.expires_at_utc > clock_timestamp())) AS "Value"
            """).SingleAsync(ct).ConfigureAwait(false);
        if (change.Status == "Active" && !targetValid) return new(HealthOfficerDesignationChangeStatus.Denied);
        var version = await db.Database.SqlQuery<long>($"""
            SELECT COALESCE(MAX(version), 0) AS "Value" FROM health_officer_designations
            WHERE actor_id = {change.TargetActorId} AND organization_id = {change.OrganizationId}
                AND country_code = {change.CountryCode} AND sector = {change.Sector}
            """).SingleAsync(ct).ConfigureAwait(false);
        if (version != change.ExpectedVersion || version == 0 && change.Status != "Active")
            return new(HealthOfficerDesignationChangeStatus.Conflict);
        var designationId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO health_officer_designations (designation_id, actor_id, organization_id, country_code, sector, status,
                valid_from_utc, expires_at_utc, designated_by_actor_id, version, created_at_utc, updated_at_utc)
            VALUES ({designationId}, {change.TargetActorId}, {change.OrganizationId}, {change.CountryCode}, {change.Sector}, {change.Status},
                {from}, {expires}, {actor.ActorId}, {version + 1}, clock_timestamp(), clock_timestamp())
            """, ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO health_officer_designation_commands (command_id, administrator_actor_id, target_actor_id, organization_id,
                country_code, sector, status, valid_from_utc, expires_at_utc, expected_version, designation_id, designation_version, recorded_at_utc)
            VALUES ({change.CommandId}, {actor.ActorId}, {change.TargetActorId}, {change.OrganizationId}, {change.CountryCode}, {change.Sector},
                {change.Status}, {from}, {expires}, {change.ExpectedVersion}, {designationId}, {version + 1}, clock_timestamp())
            """, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(HealthOfficerDesignationChangeStatus.Recorded, designationId, version + 1);
    }

    private static DateTimeOffset Microseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    private sealed class Receipt
    {
        public string Administrator { get; init; } = "";
        public string Target { get; init; } = "";
        public string Organization { get; init; } = "";
        public string Country { get; init; } = "";
        public string Sector { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTimeOffset ValidFrom { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public long ExpectedVersion { get; init; }
        public Guid DesignationId { get; init; }
        public long Version { get; init; }
    }
}
