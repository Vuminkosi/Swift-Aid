using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Identity;
using SwiftAid.Client.Presentation;
using SwiftAid.Gateway.Presentation;
using SwiftAid.Gateway.Ingestion;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class GatewayWorkspaceAccessTests
{
    [IntakePostgresFact]
    public async Task Lists_both_organizations_and_revalidates_selection_membership_and_session()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var auth = new Authentication();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<AuthenticationStateProvider>(auth);
        services.AddScoped(_ => new SwiftAidDbContext(new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql(fixture.Db.Database.GetConnectionString()).UseOpenIddict().Options));
        services.AddScoped<IOrganizationOwnerAuthorizer, PostgresOrganizationOwnerAuthorizer>();
        services.AddScoped<IOrganizationMembershipService, OrganizationMembershipService>();
        services.AddScoped<GatewayWorkspaceAccessClient>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var memberships = scope.ServiceProvider.GetRequiredService<IOrganizationMembershipService>();
        var first = "workspace-test-" + Guid.NewGuid().ToString("N");
        var second = "workspace-test-" + Guid.NewGuid().ToString("N");
        Assert.Equal(OrganizationCreateStatus.Created, (await memberships.CreateAsync("officer-1", first, default)).Status);
        Assert.Equal(OrganizationCreateStatus.Created, (await memberships.CreateAsync("officer-1", second, default)).Status);
        var client = scope.ServiceProvider.GetRequiredService<GatewayWorkspaceAccessClient>();
        var state = await client.GetStateAsync();
        Assert.True(state.Succeeded);
        Assert.Contains(state.Value!.Workspaces, w => w.Id == first + "-general");
        Assert.Contains(state.Value.Workspaces, w => w.Id == second + "-general");
        var imports = new WorkspaceImportClient(auth, provider.GetRequiredService<IServiceScopeFactory>());
        var destinations = await imports.GetDestinationsAsync(default);
        Assert.Contains(destinations, d => d.OrganizationId == first && d.WorkspaceId == first + "-general");
        Assert.Contains(destinations, d => d.OrganizationId == second && d.WorkspaceId == second + "-general");
        var grants = await fixture.Db.AccessGrantGroups.Where(g => g.OrganizationId == first).ToArrayAsync();
        Assert.Equal(4, grants.Length);
        Assert.All(grants, g =>
        {
            Assert.Equal("officer-1", g.ActorId);
            Assert.Equal(first + "-general", g.WorkspaceId);
            Assert.Equal("standard", g.SensitivityCode);
            Assert.True(g.CanAccessJurisdictionalCases);
        });
        Assert.Equal(new[] { "SZ", "ZW" }, grants.Select(g => g.CountryCode).Distinct().Order().ToArray());
        var available = await fixture.Store.GetWorkspacesAsync(CaseImportPostgresTests.Actor, default);
        Assert.Equal(2, available.Count(w => w.OrganizationId == first));
        var context = CaseImportPostgresTests.Context with { WorkspaceId = first + "-general", OrganizationId = first };
        var imported = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), context, 1, true,
            new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
        Assert.NotNull(Assert.Single(imported.Rows).CaseId);
        var unit = new SwiftAid.Infrastructure.Persistence.Transactions.PostgresCaseCommandUnitOfWork(fixture.Db);
        var policy = new CaseAccessPolicy(new SwiftAid.Infrastructure.Authorization.PostgresCurrentAccessGrantStore(fixture.Db, unit),
            new SwiftAid.Infrastructure.Authorization.PostgresCaseAccessDescriptorStore(fixture.Db, unit));
        var queue = new SwiftAid.Application.Cases.CaseQueueQuery(
            new SwiftAid.Infrastructure.Persistence.Repositories.Cases.PostgresCaseQueueStore(fixture.Db), policy, unit);
        var listed = await queue.GetMineAsync(CaseImportPostgresTests.Actor, 100, default);
        Assert.Contains(listed.Items, item => item.CaseId == imported.Rows[0].CaseId);
        var selected = await client.SelectWorkspaceAsync(second + "-general");
        Assert.Equal(second + "-general", selected.Value!.SelectedWorkspaceId);
        Assert.Equal(selected.Value.SelectedWorkspaceId, (await client.GetStateAsync()).Value!.SelectedWorkspaceId);
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.SelectWorkspaceAsync("someone-elses-workspace")).Code);

        var membership = await fixture.Db.OrganizationMemberships.SingleAsync(m => m.ActorId == "officer-1" && m.OrganizationId == second);
        membership.Status = PersistentMembershipStatus.Revoked;
        await fixture.Db.SaveChangesAsync();
        state = await client.GetStateAsync();
        Assert.DoesNotContain(state.Value!.Workspaces, w => w.Id == second + "-general");
        Assert.DoesNotContain(await imports.GetDestinationsAsync(default), d => d.OrganizationId == second);
        Assert.DoesNotContain(await fixture.Store.GetWorkspacesAsync(CaseImportPostgresTests.Actor, default), w => w.OrganizationId == second);
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.SelectWorkspaceAsync(second + "-general")).Code);
        auth.Epoch = 999999;
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.GetStateAsync()).Code);
        auth.SignedIn = false;
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.GetStateAsync()).Code);
    }

    [IntakePostgresFact]
    public async Task Creator_backfill_is_idempotent_and_preserves_restrictions_and_unproven_members()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var service = new OrganizationMembershipService(fixture.Db, TimeProvider.System,
            new PostgresOrganizationOwnerAuthorizer(fixture.Db, TimeProvider.System));
        var names = Enumerable.Range(0, 4).Select(_ => "creator-test-" + Guid.NewGuid().ToString("N")).ToArray();
        foreach (var name in names)
            Assert.Equal(OrganizationCreateStatus.Created, (await service.CreateAsync("officer-1", name, default)).Status);

        // Reproduce pre-upgrade state for a creator, a revoked member, and an unproven member.
        var cleared = names.Take(3).ToArray();
        var ids = await fixture.Db.AccessGrantGroups.Where(g => cleared.Contains(g.OrganizationId)).Select(g => g.GrantGroupId).ToArrayAsync();
        await fixture.Db.AccessGrantOperations.Where(o => ids.Contains(o.GrantGroupId)).ExecuteDeleteAsync();
        await fixture.Db.AccessGrantGroups.Where(g => ids.Contains(g.GrantGroupId)).ExecuteDeleteAsync();
        await fixture.Db.OrganizationMemberships.Where(m => m.OrganizationId == names[1])
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, PersistentMembershipStatus.Revoked));
        var unprovenTime = (await fixture.Db.Organizations.SingleAsync(o => o.OrganizationId == names[2])).CreatedAtUtc;
        await fixture.Db.SecurityAuditEntries.Where(a => a.Operation == "Organization.Created" && a.RecordedAtUtc == unprovenTime).ExecuteDeleteAsync();
        await fixture.Db.AccessGrantGroups.Where(g => g.OrganizationId == names[3])
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, PersistentGrantStatus.Revoked));
        fixture.Db.ChangeTracker.Clear();
        var migration = new SwiftAid.Infrastructure.Persistence.Migrations.OrganizationCreatorAccess();
        for (var run = 0; run < 2; run++)
            foreach (var sql in migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>())
                await fixture.Db.Database.ExecuteSqlRawAsync(sql.Sql);
        Assert.Equal(4, await fixture.Db.AccessGrantGroups.CountAsync(g => g.OrganizationId == names[0]));
        Assert.False(await fixture.Db.AccessGrantGroups.AnyAsync(g => g.OrganizationId == names[1] || g.OrganizationId == names[2]));
        Assert.All(await fixture.Db.AccessGrantGroups.Where(g => g.OrganizationId == names[3]).ToArrayAsync(),
            g => Assert.Equal(PersistentGrantStatus.Revoked, g.Status));
        var backfilledIds = await fixture.Db.AccessGrantGroups.Where(g => g.OrganizationId == names[0]).Select(g => g.GrantGroupId).ToArrayAsync();
        Assert.Equal(12, await fixture.Db.AccessGrantOperations.CountAsync(o => backfilledIds.Contains(o.GrantGroupId)));
        Assert.Equal(4, await fixture.Db.SecurityAuditEntries.CountAsync(a => a.Operation == "Organization.CreatorAccessBackfilled" && backfilledIds.Contains(a.ReasonCode!)));
    }

    private sealed class Authentication : AuthenticationStateProvider
    {
        public long Epoch { get; set; } = 7;
        public bool SignedIn { get; set; } = true;
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(SignedIn ? new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "officer-1"), new Claim(ActorContext.AccessEpochClaimType, Epoch.ToString())], "fixture")
                : new ClaimsIdentity())));
    }
}
