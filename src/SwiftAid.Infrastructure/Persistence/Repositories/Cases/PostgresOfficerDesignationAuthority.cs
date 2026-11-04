using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>Holds current scoped administrator authority stable until the caller's transaction ends.</summary>
internal static class PostgresOfficerDesignationAuthority
{
    public static bool ValidScope(string organization, string country, string sector) =>
        Identifier(organization) && country is { Length: 2 } && country.All(c => c is >= 'A' and <= 'Z') &&
        sector is "HumanHealth" or "AnimalHealth";

    public static bool Identifier(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value == value.Trim() && !value.Any(char.IsControl);

    public static async Task<bool> AcquireAsync(SwiftAidDbContext db, ActorContext actor, string target, string organization,
        string country, string sector, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(hashtextextended('officer-admin:' ||
                jsonb_build_array({actor.ActorId}, {organization}, {country}, {sector})::text, 0))
            """, ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT actor_id FROM actors WHERE actor_id IN ({actor.ActorId}, {target}) ORDER BY actor_id FOR SHARE", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT actor_id FROM actor_access_states WHERE actor_id = {actor.ActorId} FOR SHARE", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT organization_id FROM organizations WHERE organization_id = {organization} FOR SHARE", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT membership_id FROM organization_memberships WHERE organization_id = {organization}
                AND actor_id IN ({actor.ActorId}, {target}) ORDER BY membership_id FOR SHARE
            """, ct).ConfigureAwait(false);
        return await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM actors a JOIN actor_access_states state ON state.actor_id = a.actor_id
                JOIN organization_memberships m ON m.actor_id = a.actor_id AND m.organization_id = {organization}
                JOIN organizations o ON o.organization_id = m.organization_id
                JOIN health_officer_designation_administrators adm ON adm.actor_id = a.actor_id AND adm.organization_id = o.organization_id
                WHERE a.actor_id = {actor.ActorId} AND a.status = 'Active' AND (a.expires_at_utc IS NULL OR a.expires_at_utc > clock_timestamp())
                    AND state.access_epoch = {actor.AccessEpoch} AND o.status = 'Active' AND m.status = 'Active'
                    AND m.valid_from_utc <= clock_timestamp() AND (m.expires_at_utc IS NULL OR m.expires_at_utc > clock_timestamp())
                    AND adm.country_code = {country} AND adm.sector = {sector} AND adm.status = 'Active'
                    AND adm.valid_from_utc <= clock_timestamp() AND adm.expires_at_utc > clock_timestamp()
                    AND NOT EXISTS (SELECT 1 FROM health_officer_designation_administrators newer
                        WHERE newer.actor_id = adm.actor_id AND newer.organization_id = adm.organization_id
                        AND newer.country_code = adm.country_code AND newer.sector = adm.sector AND newer.version > adm.version)
            ) AS "Value"
            """).SingleAsync(ct).ConfigureAwait(false);
    }
}
