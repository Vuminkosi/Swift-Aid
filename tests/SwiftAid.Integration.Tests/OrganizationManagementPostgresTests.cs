using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Identity;
using SwiftAid.Client.Presentation;
using SwiftAid.Gateway.Presentation;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

public sealed class OrganizationManagementPostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task Owner_profile_and_setup_are_durable_and_do_not_change_case_grants()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        var assignment = new OrganizationOwnerEntity { OwnerAssignmentId = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            ActorId = fixture.ActorId, AssignedByActorId = fixture.ActorId, AssignedAtUtc = DateTimeOffset.UtcNow };
        db.OrganizationOwners.Add(assignment);
        await db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateDbContext());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IOrganizationOwnerAuthorizer, PostgresOrganizationOwnerAuthorizer>();
        await using var provider = services.BuildServiceProvider();
        var protection = new EphemeralDataProtectionProvider();
        var selection = new OrganizationManagementContext { OrganizationId = fixture.OrganizationId };
        var auth = new Authentication(fixture.ActorId, fixture.AccessEpoch);
        OrganizationManagementGatewayClient Client() => new(auth, provider.GetRequiredService<IServiceScopeFactory>(), selection,
            protection, NullLogger<OrganizationManagementGatewayClient>.Instance);
        var before = await db.AccessGrantGroups.Where(row => row.OrganizationId == fixture.OrganizationId)
            .Select(row => new { row.GrantGroupId, row.Status }).ToArrayAsync();
        try
        {
            var update = new OrganizationProfileUpdate("Synthetic field team", OrganizationCountry.Eswatini, OrganizationRegion.Manzini, "Correct service profile");
            var saved = await Client().UpdateProfileAsync(update);
            Assert.Equal(UiCapabilityResultCode.Success, saved.Code);
            Assert.Equal(saved.Value, (await Client().GetProfileAsync()).Value);
            var encrypted = await db.Set<OrganizationAdministrationEntity>().AsNoTracking().SingleAsync(row => row.OrganizationId == fixture.OrganizationId);
            Assert.DoesNotContain(update.DisplayName, encrypted.ProtectedState);
            Assert.Equal(OrganizationSsoStatus.SetupPending, (await Client().RequestSsoSetupAsync("Request tenant configuration")).Value!.Status);
            Assert.False((await Client().GetSsoConfigurationAsync()).Value!.HasStoredSecret);
            var preview = await Client().SendInvitationAsync(new("synthetic@example.invalid", OrganizationRole.Member,
                OrganizationCountry.Eswatini, OrganizationRegion.Manzini, "Preview membership invitation"));
            Assert.Equal(UiCapabilityResultCode.Success, preview.Code);
            Assert.Contains("not sent", preview.Value!.Status);
            Assert.NotEmpty((await Client().GetAuditAsync()).Value!);
            Assert.Equal(UiCapabilityResultCode.InvalidRequest, (await Client().UpdateProfileAsync(update with { Region = OrganizationRegion.Harare })).Code);
            Assert.Equal(UiCapabilityResultCode.Conflict, (await Client().RevokeMembershipAsync(new(fixture.MembershipId.ToString("D"), "Cannot revoke owner"))).Code);
            Assert.Equal(before, await db.AccessGrantGroups.Where(row => row.OrganizationId == fixture.OrganizationId)
                .Select(row => new { row.GrantGroupId, row.Status }).ToArrayAsync());
            await db.OrganizationOwners.Where(row => row.OwnerAssignmentId == assignment.OwnerAssignmentId)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.RevokedAtUtc, DateTimeOffset.UtcNow));
            Assert.Equal(UiCapabilityResultCode.Denied, (await Client().UpdateProfileAsync(update)).Code);
            selection.OrganizationId = "other-organization";
            Assert.Equal(UiCapabilityResultCode.Denied, (await Client().GetProfileAsync()).Code);
        }
        finally
        {
            await db.Set<OrganizationAdministrationEntity>().Where(row => row.OrganizationId == fixture.OrganizationId).ExecuteDeleteAsync();
            await db.OrganizationOwners.Where(row => row.OwnerAssignmentId == assignment.OwnerAssignmentId).ExecuteDeleteAsync();
        }
    }

    private sealed class Authentication(string actor, long epoch) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", actor), new Claim(ActorContext.AccessEpochClaimType, epoch.ToString())], "fixture"))));
    }
}