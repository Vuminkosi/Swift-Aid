using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Classification;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Integration.Tests;

public sealed class CaseClassificationGatewayClientTests
{
    [Fact]
    public async Task Concurrent_reads_use_separate_scopes_and_trusted_identity()
    {
        var queries = new List<Query>();
        using var services = new ServiceCollection().AddScoped<ICaseClassificationQuery>(_ =>
        {
            var query = new Query(); queries.Add(query); return query;
        }).BuildServiceProvider();
        var client = new CaseClassificationGatewayClient(new Auth(true), services.GetRequiredService<IServiceScopeFactory>());
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var results = await Task.WhenAll(ids.Select(id => client.GetAsync(id)));
        Assert.All(results, result => Assert.Equal(CaseClassificationAccess.Available, result.Access));
        Assert.Equal(ids, results.Select(r => r.CaseId));
        Assert.Equal(2, queries.Count);
        Assert.All(queries, query => { Assert.Equal("trusted-actor", query.Actor); Assert.True(query.Disposed); });
    }

    [Fact]
    public async Task Anonymous_reads_do_not_resolve_evidence_query()
    {
        using var services = new ServiceCollection().AddScoped<ICaseClassificationQuery>(_ => throw new InvalidOperationException("Must not resolve")).BuildServiceProvider();
        var result = await new CaseClassificationGatewayClient(new Auth(false), services.GetRequiredService<IServiceScopeFactory>()).GetAsync(Guid.NewGuid());
        Assert.Equal(CaseClassificationAccess.Denied, result.Access);
        Assert.Empty(result.Items);
    }

    private sealed class Auth(bool signedIn) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            signedIn ? new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "trusted-actor"), new(ActorContext.AccessEpochClaimType, "0")], "fixture")) : new ClaimsPrincipal()));
    }
    private sealed class Query : ICaseClassificationQuery, IDisposable
    {
        public string? Actor { get; private set; }
        public bool Disposed { get; private set; }
        public async Task<IReadOnlyList<CaseClassificationEvidence>?> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct)
        {
            Actor = actor.ActorId;
            await Task.Yield();
            return [];
        }
        public void Dispose() => Disposed = true;
    }
}
