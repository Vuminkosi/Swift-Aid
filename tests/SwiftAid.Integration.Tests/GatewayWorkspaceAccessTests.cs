using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
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
        Assert.False(await fixture.Db.AccessGrantGroups.AnyAsync(g => g.OrganizationId == first || g.OrganizationId == second));
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
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.SelectWorkspaceAsync(second + "-general")).Code);
        auth.Epoch = 999999;
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.GetStateAsync()).Code);
        auth.SignedIn = false;
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.GetStateAsync()).Code);
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
