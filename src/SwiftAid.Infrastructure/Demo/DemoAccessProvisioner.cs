using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SwiftAid.Application.Authorization;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Demo;

/// <summary>Persists explicit demo-workspace grants for authenticated demo participants.</summary>
public sealed class DemoAccessProvisioner(
    SwiftAidDbContext db, IConfiguration configuration, IHostEnvironment environment, TimeProvider clock)
{
    public async Task EnsureAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("DemoAccess:Enabled")) return;
        if (!environment.IsDevelopment())
            throw new InvalidOperationException("Demo access requires the Development environment.");

        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize provisioning with other authority changes for this actor.
        var identity = await db.Actors.FromSqlInterpolated(
            $"SELECT * FROM actors WHERE actor_id = {actor.ActorId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (identity is null || identity.Status != PersistentActorStatus.Active || identity.ExpiresAtUtc <= now
            || !await db.ActorAccessStates.AnyAsync(row => row.ActorId == actor.ActorId
                && row.AccessEpoch == actor.AccessEpoch, cancellationToken)) return;

        const string organizationId = DemoCaseImporter.DemoOrganizationId;
        const string workspaceId = DemoCaseImporter.DemoWorkspaceId;
        if (!await db.Organizations.AnyAsync(row => row.OrganizationId == organizationId
                && row.Status == PersistentResourceStatus.Active, cancellationToken)
            || !await db.Workspaces.AnyAsync(row => row.WorkspaceId == workspaceId
                && row.Status == PersistentResourceStatus.Active, cancellationToken)) return;

        var link = await db.WorkspaceOrganizations.SingleOrDefaultAsync(row =>
            row.OrganizationId == organizationId && row.WorkspaceId == workspaceId, cancellationToken);
        if (link is null)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO workspace_organizations (organization_id, workspace_id, status, created_at_utc, updated_at_utc)
                VALUES ({organizationId}, {workspaceId}, 'Active', {now}, {now})
                ON CONFLICT DO NOTHING
                """, cancellationToken);
        else if (link.Status != PersistentResourceStatus.Active) return;

        var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(row =>
            row.ActorId == actor.ActorId && row.OrganizationId == organizationId, cancellationToken);
        if (membership is null)
        {
            membership = new OrganizationMembershipEntity
            {
                MembershipId = Guid.NewGuid(), ActorId = actor.ActorId, OrganizationId = organizationId,
                Status = PersistentMembershipStatus.Active, ValidFromUtc = now,
                GrantedByActorId = DemoCaseImporter.DemoActorId, ChangeReason = "Explicit development demo access", Version = 1
            };
            db.OrganizationMemberships.Add(membership);
        }
        else if (membership.Status != PersistentMembershipStatus.Active || membership.ValidFromUtc > now
            || membership.ExpiresAtUtc <= now) return;

        var scopes = await db.ManagedCases.AsNoTracking().Where(row =>
                row.OrganizationId == organizationId && row.WorkspaceId == workspaceId)
            .Select(row => new { row.CountryCode, row.Sector, row.SensitivityCode }).Distinct().ToArrayAsync(cancellationToken);
        foreach (var scope in scopes)
        {
            var id = $"demo-access:{actor.ActorId}:{scope.CountryCode}:{scope.Sector}:{scope.SensitivityCode}";
            if (await db.AccessGrantGroups.AnyAsync(row => row.GrantGroupId == id, cancellationToken)) continue;
            db.AccessGrantGroups.Add(new AccessGrantGroupEntity
            {
                GrantGroupId = id, MembershipId = membership.MembershipId, ActorId = actor.ActorId,
                OrganizationId = organizationId, WorkspaceId = workspaceId, CountryCode = scope.CountryCode,
                Sector = scope.Sector, SensitivityCode = scope.SensitivityCode, CanAccessJurisdictionalCases = true,
                Status = PersistentGrantStatus.Active, ValidFromUtc = now, ExpiresAtUtc = now.AddDays(30),
                GrantedByActorId = DemoCaseImporter.DemoActorId, ChangeReason = "Explicit development demo access", Version = 1
            });
            foreach (var operation in Enum.GetValues<CaseOperation>())
                db.AccessGrantOperations.Add(new AccessGrantOperationEntity { GrantGroupId = id, Operation = operation });
            var regions = await (from region in db.ManagedCaseRegions
                                 join resource in db.ManagedCases on region.CaseId equals resource.CaseId
                                 where resource.OrganizationId == organizationId && resource.WorkspaceId == workspaceId
                                     && resource.CountryCode == scope.CountryCode && resource.Sector == scope.Sector
                                     && resource.SensitivityCode == scope.SensitivityCode
                                 select region.RegionId).Distinct().ToArrayAsync(cancellationToken);
            foreach (var region in regions)
                db.AccessGrantRegions.Add(new AccessGrantRegionEntity { GrantGroupId = id, RegionId = region });
        }
        var caseIds = await db.ManagedCases.Where(row => row.OrganizationId == organizationId
            && row.WorkspaceId == workspaceId && !db.ManagedCaseRegions.Any(region => region.CaseId == row.CaseId))
            .Select(row => row.CaseId).ToArrayAsync(cancellationToken);
        var existingShares = await db.CaseShares.Where(row => row.ActorId == actor.ActorId)
            .Select(row => row.CaseId).ToArrayAsync(cancellationToken);
        foreach (var caseId in caseIds.Except(existingShares))
            db.CaseShares.Add(new CaseShareEntity
            {
                CaseId = caseId, ActorId = actor.ActorId, IsActive = true, Version = 1,
                GrantedByActorId = DemoCaseImporter.DemoActorId, GrantedAtUtc = now,
                ExpiresAtUtc = now.AddDays(30), ChangeReason = "Explicit development demo access",
                // A legacy full share uses the operations of the correlated grant above.
                AllowedOperations = null
            });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
