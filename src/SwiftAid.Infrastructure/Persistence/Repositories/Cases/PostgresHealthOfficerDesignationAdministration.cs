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
        if (change.CommandId == Guid.Empty || !Identifier(change.TargetActorId) || !Identifier(change.OrganizationId) ||
            change.TargetActorId == actor.ActorId || change.CountryCode is not { Length: 2 } ||
            change.CountryCode.Any(c => c is < 'A' or > 'Z') || change.Sector is not ("HumanHealth" or "AnimalHealth") ||
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
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(hashtextextended('officer-admin:' ||
                jsonb_build_array({actor.ActorId}, {change.OrganizationId}, {change.CountryCode}, {change.Sector})::text, 0))
            """, ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT actor_id FROM actors WHERE actor_id IN ({actor.ActorId}, {change.TargetActorId}) ORDER BY actor_id FOR SHARE", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT actor_id FROM actor_access_states WHERE actor_id = {actor.ActorId} FOR SHARE", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT organization_id FROM organizations WHERE organization_id = {change.OrganizationId} FOR SHARE", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT membership_id FROM organization_memberships WHERE organization_id = {change.OrganizationId}
                AND actor_id IN ({actor.ActorId}, {change.TargetActorId}) ORDER BY membership_id FOR SHARE
            """, ct).ConfigureAwait(false);

        var authorized = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM actors a JOIN actor_access_states state ON state.actor_id = a.actor_id
                JOIN organization_memberships m ON m.actor_id = a.actor_id AND m.organization_id = {change.OrganizationId}
                JOIN organizations o ON o.organization_id = m.organization_id
                JOIN health_officer_designation_administrators adm ON adm.actor_id = a.actor_id AND adm.organization_id = o.organization_id
                WHERE a.actor_id = {actor.ActorId} AND a.status = 'Active' AND (a.expires_at_utc IS NULL OR a.expires_at_utc > clock_timestamp())
                    AND state.access_epoch = {actor.AccessEpoch} AND o.status = 'Active' AND m.status = 'Active'
                    AND m.valid_from_utc <= clock_timestamp() AND (m.expires_at_utc IS NULL OR m.expires_at_utc > clock_timestamp())
                    AND adm.country_code = {change.CountryCode} AND adm.sector = {change.Sector} AND adm.status = 'Active'
                    AND adm.valid_from_utc <= clock_timestamp() AND adm.expires_at_utc > clock_timestamp()
                    AND NOT EXISTS (SELECT 1 FROM health_officer_designation_administrators newer
                        WHERE newer.actor_id = adm.actor_id AND newer.organization_id = adm.organization_id
                        AND newer.country_code = adm.country_code AND newer.sector = adm.sector AND newer.version > adm.version)
            ) AS "Value"
            """).SingleAsync(ct).ConfigureAwait(false);
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

    private static bool Identifier(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value == value.Trim() && !value.Any(char.IsControl);
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
