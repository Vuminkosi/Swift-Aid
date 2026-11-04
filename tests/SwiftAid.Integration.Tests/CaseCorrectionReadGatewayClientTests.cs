using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Client.Presentation;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Integration.Tests;

public sealed class CaseCorrectionReadGatewayClientTests
{
    [Fact]
    public async Task Get_DerivesActorAndMapsAuthorizedProjection()
    {
        var id = Guid.NewGuid();
        var query = new StubQuery(new CaseCorrectionReadResult(CaseCorrectionReadAccess.Allowed,
            new CaseCorrectionReadData(id, 2, 1, CaseCorrectionReportKind.LegacyV1, true, "original report")));
        using var services = new ServiceCollection().AddSingleton<ICaseCorrectionReadQuery>(query).BuildServiceProvider();
        var client = new CaseCorrectionReadGatewayClient(new AuthProvider(), services);
        var result = await client.GetAsync(id);
        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        Assert.Equal("trusted-actor", query.Actor!.ActorId);
        Assert.Equal("original report", result.Value!.Narrative);
    }

    [Fact]
    public async Task Get_DeniedAndUnavailableNeverExposeNarrative()
    {
        var id = Guid.NewGuid();
        using var deniedServices = new ServiceCollection().AddSingleton<ICaseCorrectionReadQuery>(
            new StubQuery(new CaseCorrectionReadResult(CaseCorrectionReadAccess.NotFoundOrNotAuthorized, null))).BuildServiceProvider();
        var denied = await new CaseCorrectionReadGatewayClient(new AuthProvider(), deniedServices).GetAsync(id);
        Assert.Equal(UiCapabilityResultCode.Denied, denied.Code);
        Assert.DoesNotContain("private", denied.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        using var unavailableServices = new ServiceCollection().AddSingleton<ICaseCorrectionReadQuery>(
            new StubQuery(new CaseCorrectionReadResult(CaseCorrectionReadAccess.Unavailable, null))).BuildServiceProvider();
        var unavailable = await new CaseCorrectionReadGatewayClient(new AuthProvider(), unavailableServices).GetAsync(id);
        Assert.Equal(UiCapabilityResultCode.Unavailable, unavailable.Code);
    }

    [Fact]
    public async Task Get_ReturnsUnavailableWhenQueryServiceResolutionThrows()
    {
        var client = new CaseCorrectionReadGatewayClient(new AuthProvider(), new ThrowingServiceProvider());
        var result = await client.GetAsync(Guid.NewGuid());
        Assert.Equal(UiCapabilityResultCode.Unavailable, result.Code);
        Assert.Null(result.Value);
    }

    private sealed class AuthProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "trusted-actor"),
                new Claim(ActorContext.AccessEpochClaimType, "0")
            ], "test"))));
    }

    private sealed class StubQuery(CaseCorrectionReadResult result) : ICaseCorrectionReadQuery
    {
        public ActorContext? Actor { get; private set; }
        public Task<CaseCorrectionReadResult> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct)
        {
            Actor = actor;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => throw new InvalidOperationException("Factory failed.");
    }
}
