using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

public sealed partial class CaseCommandProcessingPostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task OfficerCandidateHttpEndpointUsesCurrentCaseAuthorityAndDesignation()
    {
        // Append-only designation records require a disposable fixture database.
        var fixture = await CaseAuthorityPostgresFixture.CreateAsync();
        var targetGrant = await AddEligibleTargetGrantAsync(fixture);
        await AddOperationAsync(fixture, CaseOperation.ManageEscalation);
        await using (var db = fixture.CreateDbContext())
        {
            await db.AccessGrantGroups.Where(group => group.GrantGroupId == targetGrant)
                .ExecuteUpdateAsync(update => update.SetProperty(group => group.CanAccessJurisdictionalCases, true));
            db.AccessGrantRegions.Add(new AccessGrantRegionEntity { GrantGroupId = targetGrant, RegionId = fixture.CaseRegionId });
            db.AccessGrantOperations.AddRange(
                new AccessGrantOperationEntity { GrantGroupId = targetGrant, Operation = CaseOperation.ManageEscalation },
                new AccessGrantOperationEntity { GrantGroupId = targetGrant, Operation = CaseOperation.ManageFollowUp });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO health_officer_designations (designation_id, actor_id, organization_id, country_code, sector, status,
                    valid_from_utc, expires_at_utc, designated_by_actor_id, version, created_at_utc, updated_at_utc)
                VALUES ({Guid.NewGuid()}, {fixture.NewOwnerActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Active',
                    clock_timestamp() - interval '1 day', clock_timestamp() + interval '1 day', {fixture.ActorId}, 1, clock_timestamp(), clock_timestamp())
                """);
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication()
            .AddScheme<CollaboratorTestAuthenticationOptions, CollaboratorTestAuthenticationHandler>(IdentityConstants.ApplicationScheme, _ => { })
            .AddScheme<CollaboratorTestAuthenticationOptions, CollaboratorTestAuthenticationHandler>(
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, options =>
                {
                    options.Principals["current"] = Principal(fixture.ActorId, fixture.AccessEpoch);
                    options.Principals["stale"] = Principal(fixture.ActorId, fixture.AccessEpoch - 1);
                });
        builder.Services.AddAuthorization();
        var settings = new HealthOfficerDirectoryOptions { Enabled = true };
        builder.Services.AddScoped(_ => fixture.CreateDbContext());
        builder.Services.AddScoped<IEligibleHealthOfficerQuery>(services =>
        {
            var db = services.GetRequiredService<SwiftAidDbContext>();
            var unit = new PostgresCaseCommandUnitOfWork(db);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
            return new EligibleHealthOfficerQuery(new PostgresEligibleHealthOfficerStore(db, unit), policy, unit, settings);
        });
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEligibleHealthOfficerEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        async Task<HttpResponseMessage> Get(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cases/{fixture.CaseId:D}/eligible-health-officers");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(request);
        }

        using (var response = await Get("current"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<EligibleHealthOfficerCandidatePage>();
            Assert.Equal(fixture.NewOwnerActorId, Assert.Single(page!.Items).ActorId);
        }
        using (var response = await Get("stale")) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        settings.Enabled = false;
        using (var response = await Get("current")) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        settings.Enabled = true;
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO health_officer_designations (designation_id, actor_id, organization_id, country_code, sector, status,
                    valid_from_utc, expires_at_utc, designated_by_actor_id, version, created_at_utc, updated_at_utc)
                VALUES ({Guid.NewGuid()}, {fixture.NewOwnerActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Revoked',
                    clock_timestamp() - interval '1 day', clock_timestamp() + interval '1 day', {fixture.ActorId}, 2, clock_timestamp(), clock_timestamp())
                """);
        }
        using (var response = await Get("current"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty((await response.Content.ReadFromJsonAsync<EligibleHealthOfficerCandidatePage>())!.Items);
        }
    }
}
