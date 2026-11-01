using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Contracts.Commands;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class SharedWorkspaceImportTests
{
    [IntakePostgresFact]
    public async Task Two_organizations_import_into_one_workspace_without_sharing_case_authority()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var linked = await AddOrganizationAsync(fixture.Db);
        var original = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), CaseImportPostgresTests.Context,
            1, true, new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
        var context = CaseImportPostgresTests.Context with { OrganizationId = linked.Organization };
        var imported = await fixture.Service.ProcessAsync(linked.Actor, Guid.NewGuid(), context,
            1, true, new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
        Assert.Equal("Accepted", Assert.Single(imported.Rows).Status);
        var caseId = imported.Rows[0].CaseId!.Value;
        var stored = await fixture.Db.ManagedCases.AsNoTracking().SingleAsync(row => row.CaseId == caseId);
        Assert.Equal("workspace-1", stored.WorkspaceId);
        Assert.Equal(linked.Organization, stored.OrganizationId);
        Assert.Equal(linked.Actor.ActorId, stored.AccountableOwnerActorId);
        Assert.Contains(await fixture.Store.GetWorkspacesAsync(linked.Actor, default), scope => scope.OrganizationId == linked.Organization && scope.WorkspaceId == "workspace-1");
        Assert.True(await CanReadAsync(linked.Actor, caseId));
        Assert.False(await CanReadAsync(CaseImportPostgresTests.Actor, caseId));
        Assert.False(await CanReadAsync(linked.Actor, original.Rows[0].CaseId!.Value));
        Assert.Null(await fixture.Store.GetAsync(CaseImportPostgresTests.Actor, imported.ImportId, default));

        await fixture.Db.WorkspaceOrganizations.Where(row => row.WorkspaceId == "workspace-1" && row.OrganizationId == linked.Organization)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, PersistentResourceStatus.Suspended));
        Assert.False(await CanReadAsync(linked.Actor, caseId));
        Assert.Empty(await fixture.Store.GetWorkspacesAsync(linked.Actor, default));
        await Assert.ThrowsAsync<CaseImportException>(() => fixture.Store.GetAsync(linked.Actor, imported.ImportId, default));
        Assert.True(await CanReadAsync(CaseImportPostgresTests.Actor, original.Rows[0].CaseId!.Value));
    }

    [IntakePostgresFact]
    public async Task Changing_selected_organization_cannot_replay_a_command_into_another_organization()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var linked = await AddOrganizationAsync(fixture.Db, CaseImportPostgresTests.Actor.ActorId);
        var command = Guid.NewGuid();
        var csv = CaseImportPostgresTests.Csv(command);
        var first = await fixture.Service.ProcessAsync(linked.Actor, Guid.NewGuid(), CaseImportPostgresTests.Context with { OrganizationId = "org-zw-1" }, 1, true, new StringReader(csv), default);
        Assert.Equal("Accepted", Assert.Single(first.Rows).Status);
        var second = await fixture.Service.ProcessAsync(linked.Actor, Guid.NewGuid(), CaseImportPostgresTests.Context with { OrganizationId = linked.Organization }, 1, true, new StringReader(csv), default);
        Assert.Equal("Conflict", Assert.Single(second.Rows).Status);
        Assert.Null(second.Rows[0].CaseId);
        Assert.Equal(1, await fixture.Db.CommandReceipts.CountAsync(row => row.CommandId == command));
        Assert.Equal(2, await fixture.Db.CaseEvents.CountAsync(row => row.CommandId == command));
    }

    [IntakePostgresFact]
    public async Task Link_revocation_waits_for_active_authority_scope_and_then_denies_new_work()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var linked = await AddOrganizationAsync(fixture.Db);
        await using var transaction = await fixture.Db.Database.BeginTransactionAsync();
        Assert.True(await fixture.Db.Database.SqlQuery<bool>($"SELECT swiftaid_internal.lock_workspace_organization({"workspace-1"}, {linked.Organization}) AS \"Value\"").SingleAsync());
        await using var concurrent = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var revoke = concurrent.WorkspaceOrganizations.Where(row => row.WorkspaceId == "workspace-1" && row.OrganizationId == linked.Organization)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, PersistentResourceStatus.Revoked));
        Assert.NotSame(revoke, await Task.WhenAny(revoke, Task.Delay(150)));
        await transaction.CommitAsync();
        Assert.Equal(1, await revoke.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(await fixture.Store.GetWorkspacesAsync(linked.Actor, default));
        await Assert.ThrowsAsync<CaseImportException>(() => fixture.Service.ProcessAsync(linked.Actor, Guid.NewGuid(),
            CaseImportPostgresTests.Context with { OrganizationId = linked.Organization }, 1, true,
            new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default));
    }

    [IntakePostgresFact]
    public async Task Sharing_a_workspace_does_not_grant_membership_or_case_permissions()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var linked = await AddOrganizationAsync(fixture.Db);
        var denied = await Assert.ThrowsAsync<CaseImportException>(() => fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(),
            CaseImportPostgresTests.Context with { OrganizationId = linked.Organization }, 1, true,
            new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default));
        Assert.Equal("not-authorized", denied.Code);
        // Legacy requests keep their original organization; no implicit switch to a new link.
        await Assert.ThrowsAsync<CaseImportException>(() => fixture.Service.ProcessAsync(linked.Actor, Guid.NewGuid(), CaseImportPostgresTests.Context,
            1, true, new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default));
    }

    internal static async Task<(string Organization, ActorContext Actor)> AddOrganizationAsync(SwiftAidDbContext db, string? existingActor = null)
    {
        var organization = "shared-org-" + Guid.NewGuid().ToString("N");
        var actor = existingActor ?? "shared-actor-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        if (existingActor is null)
        {
            db.Actors.Add(new ActorEntity { ActorId = actor, Status = PersistentActorStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now });
            db.ActorAccessStates.Add(new ActorAccessStateEntity { ActorId = actor, AccessEpoch = 7, Version = 1, UpdatedAtUtc = now });
        }
        db.Organizations.Add(new OrganizationEntity { OrganizationId = organization, Status = PersistentResourceStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now });
        db.WorkspaceOrganizations.Add(new WorkspaceOrganizationEntity { WorkspaceId = "workspace-1", OrganizationId = organization,
            Status = PersistentResourceStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now });
        var membership = Guid.NewGuid();
        db.OrganizationMemberships.Add(new OrganizationMembershipEntity { MembershipId = membership, OrganizationId = organization, ActorId = actor,
            Status = PersistentMembershipStatus.Active, ValidFromUtc = now.AddHours(-1), GrantedByActorId = "granter-1", Version = 1 });
        var grant = "shared-grant-" + Guid.NewGuid().ToString("N");
        db.AccessGrantGroups.Add(new AccessGrantGroupEntity { GrantGroupId = grant, MembershipId = membership, OrganizationId = organization,
            WorkspaceId = "workspace-1", ActorId = actor, CountryCode = "ZW", Sector = Sector.HumanHealth, SensitivityCode = "standard",
            Status = PersistentGrantStatus.Active, ValidFromUtc = now.AddHours(-1), ExpiresAtUtc = now.AddHours(1), GrantedByActorId = "granter-1", Version = 1 });
        db.AccessGrantOperations.Add(new AccessGrantOperationEntity { GrantGroupId = grant, Operation = CaseOperation.ReadCase });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (organization, CaseRegistrationBackendTests.CreateOfficer(actor));
    }

    private static async Task<bool> CanReadAsync(ActorContext actor, Guid caseId)
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var unit = new PostgresCaseCommandUnitOfWork(db);
        await using var lease = await unit.BeginCaseAsync(actor, caseId, [], CaseLeaseMode.Read, default);
        var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
        return (await policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, default)).IsAllowed;
    }
}
