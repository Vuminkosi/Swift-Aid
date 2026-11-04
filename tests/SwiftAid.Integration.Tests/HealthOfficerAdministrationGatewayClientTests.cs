using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Integration.Tests;

public sealed class HealthOfficerAdministrationGatewayClientTests
{
    [Fact]
    public async Task UsesAuthenticatedActorSeparateScopeAndPreservesUnknownOutcome()
    {
        var servicesCreated = new List<Service>();
        await using var provider = new ServiceCollection().AddScoped<IHealthOfficerDesignationAdministration>(_ =>
        {
            var service = new Service(); servicesCreated.Add(service); return service;
        }).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var client = new HealthOfficerAdministrationGatewayClient(new Auth(true), provider);
        var request = new HealthOfficerDesignationChangeRequest { CommandId = Guid.NewGuid(), TargetActorId = "target", OrganizationId = "org",
            CountryCode = "SZ", Sector = "HumanHealth", Status = "Active", ValidFromUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) };
        var first = await client.ChangeAsync(request);
        Assert.Equal(HealthOfficerAdministrationStatus.Recorded, first.Status);
        Assert.Equal("trusted", servicesCreated[0].Actor!.ActorId);
        Assert.Equal(request.CommandId, servicesCreated[0].Change!.CommandId);
        Assert.True(servicesCreated[0].Disposed);
        Service.Fail = true;
        try { Assert.Equal(HealthOfficerAdministrationStatus.OutcomeUnknown, (await client.ChangeAsync(request)).Status); }
        finally { Service.Fail = false; }
        Assert.True(servicesCreated[1].Disposed);
        var anonymous = new HealthOfficerAdministrationGatewayClient(new Auth(false), provider);
        Assert.Equal(HealthOfficerAdministrationStatus.Denied, (await anonymous.ChangeAsync(request)).Status);
        Assert.Equal(2, servicesCreated.Count);
    }

    private sealed class Auth(bool authenticated) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "trusted"), new Claim(ActorContext.AccessEpochClaimType, "1")], authenticated ? "fixture" : null))));
    }

    private sealed class Service : IHealthOfficerDesignationAdministration, IAsyncDisposable
    {
        public static bool Fail { get; set; }
        public bool Disposed { get; private set; }
        public ActorContext? Actor { get; private set; }
        public HealthOfficerDesignationChange? Change { get; private set; }
        public Task<HealthOfficerDesignationChangeResult> ChangeAsync(ActorContext actor, HealthOfficerDesignationChange change, CancellationToken ct)
        {
            Actor = actor; Change = change;
            if (Fail) throw new InvalidOperationException("Synthetic unknown result");
            return Task.FromResult(new HealthOfficerDesignationChangeResult(HealthOfficerDesignationChangeStatus.Recorded, Guid.NewGuid(), 1));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
