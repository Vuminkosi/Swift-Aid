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
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;

namespace SwiftAid.Integration.Tests;

public sealed partial class CaseCommandProcessingPostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task DesignationHttpRequiresCurrentScopedAdministratorAndPersistsReplay()
    {
        // The fixture's immutable role records are removed with the disposable database.
        var fixture = await CaseAuthorityPostgresFixture.CreateAsync();
        await AddEligibleTargetGrantAsync(fixture);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication()
            .AddScheme<CollaboratorTestAuthenticationOptions, CollaboratorTestAuthenticationHandler>(IdentityConstants.ApplicationScheme, _ => { })
            .AddScheme<CollaboratorTestAuthenticationOptions, CollaboratorTestAuthenticationHandler>(
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, options =>
                {
                    options.Principals["current"] = Principal(fixture.ActorId, fixture.AccessEpoch);
                    options.Principals["stale"] = Principal(fixture.ActorId, fixture.AccessEpoch - 1);
                });
        builder.Services.AddAuthorization();
        var settings = new HealthOfficerDirectoryOptions { Enabled = true, AdministrationEnabled = true };
        builder.Services.AddScoped(_ => fixture.CreateDbContext());
        builder.Services.AddScoped<IHealthOfficerDesignationAdministration>(services =>
            new PostgresHealthOfficerDesignationAdministration(services.GetRequiredService<SwiftAidDbContext>(), settings));
        builder.Services.AddScoped<IHealthOfficerDesignationQuery>(services =>
            new PostgresHealthOfficerDesignationQuery(services.GetRequiredService<SwiftAidDbContext>(), settings));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthOfficerDesignationEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var request = new HealthOfficerDesignationChangeRequest
        {
            CommandId = Guid.NewGuid(), TargetActorId = fixture.NewOwnerActorId, OrganizationId = fixture.OrganizationId,
            CountryCode = "SZ", Sector = "HumanHealth", Status = "Active", ValidFromUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1), ExpectedVersion = 0
        };
        async Task<HttpResponseMessage> Send(string token, HealthOfficerDesignationChangeRequest payload)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/health-officer-designations/changes") { Content = JsonContent.Create(payload) };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(message);
        }
        async Task<HttpResponseMessage> Lookup(string token, string sector = "HumanHealth")
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/health-officer-designations/lookup")
            {
                Content = JsonContent.Create(new HealthOfficerDesignationLookupRequest
                { TargetActorId = fixture.NewOwnerActorId, OrganizationId = fixture.OrganizationId, CountryCode = "SZ", Sector = sector })
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(message);
        }
        using (var denied = await Send("current", request)) Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using (var denied = await Lookup("current")) Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO health_officer_designation_administrators (administrator_event_id, actor_id, organization_id, country_code,
                    sector, status, valid_from_utc, expires_at_utc, granted_by_actor_id, version, recorded_at_utc)
                VALUES ({Guid.NewGuid()}, {fixture.ActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Active',
                    clock_timestamp() - interval '1 day', clock_timestamp() + interval '1 day', {fixture.OwnerActorId}, 1, clock_timestamp())
                """);
        }
        using (var stale = await Send("stale", request)) Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        using (var stale = await Lookup("stale")) Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        using (var wrongSector = await Lookup("current", "AnimalHealth")) Assert.Equal(HttpStatusCode.NotFound, wrongSector.StatusCode);
        using (var empty = await Lookup("current"))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            Assert.True(empty.Headers.CacheControl!.NoStore);
            Assert.Null((await empty.Content.ReadFromJsonAsync<HealthOfficerDesignationLookupResponse>())!.Current);
        }
        Guid designationId;
        using (var created = await Send("current", request))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var receipt = (await created.Content.ReadFromJsonAsync<HealthOfficerDesignationChangeReceipt>())!;
            Assert.False(receipt.Replayed);
            Assert.Equal(1, receipt.Version);
            designationId = receipt.DesignationId;
        }
        using (var replay = await Send("current", request))
        {
            var receipt = (await replay.Content.ReadFromJsonAsync<HealthOfficerDesignationChangeReceipt>())!;
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.True(receipt.Replayed);
            Assert.Equal(designationId, receipt.DesignationId);
        }
        using (var current = await Lookup("current"))
        {
            var designation = (await current.Content.ReadFromJsonAsync<HealthOfficerDesignationLookupResponse>())!.Current;
            Assert.Equal(designationId, designation!.DesignationId);
            Assert.Equal(1, designation.Version);
            Assert.Equal("Active", designation.Status);
        }
        var revoke = new HealthOfficerDesignationChangeRequest
        {
            CommandId = Guid.NewGuid(), TargetActorId = request.TargetActorId, OrganizationId = request.OrganizationId,
            CountryCode = request.CountryCode, Sector = request.Sector, Status = "Revoked",
            ValidFromUtc = request.ValidFromUtc, ExpiresAtUtc = request.ExpiresAtUtc, ExpectedVersion = 1
        };
        using (var revoked = await Send("current", revoke)) Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        using (var current = await Lookup("current"))
        {
            var designation = (await current.Content.ReadFromJsonAsync<HealthOfficerDesignationLookupResponse>())!.Current;
            Assert.Equal(2, designation!.Version);
            Assert.Equal("Revoked", designation.Status);
        }
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO health_officer_designation_administrators (administrator_event_id, actor_id, organization_id, country_code,
                    sector, status, valid_from_utc, expires_at_utc, granted_by_actor_id, version, recorded_at_utc)
                VALUES ({Guid.NewGuid()}, {fixture.ActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Revoked',
                    clock_timestamp() - interval '1 day', clock_timestamp() + interval '1 day', {fixture.OwnerActorId}, 2, clock_timestamp())
                """);
        }
        using (var denied = await Lookup("current")) Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        settings.AdministrationEnabled = false;
        using (var disabled = await Send("current", request)) Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        await using var verification = fixture.CreateDbContext();
        var count = await verification.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM health_officer_designation_commands WHERE command_id = {request.CommandId}").SingleAsync();
        Assert.Equal(1, count);
    }
}
