using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Integration.Tests;

public sealed class CaseCollaboratorCandidatesGatewayClientTests
{
    [Fact]
    public async Task Get_UsesValidatedActorAndDisposesSeparateScopeForEachRequest()
    {
        var queries = new List<Query>();
        await using var services = new ServiceCollection()
            .AddScoped<ICaseCollaboratorCandidatesQuery>(_ =>
            {
                var query = new Query();
                queries.Add(query);
                return query;
            }).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var client = new CaseCollaboratorCandidatesGatewayClient(new AuthProvider(true), services);
        var id = Guid.NewGuid();

        Assert.NotNull(await client.GetAsync(id, "Colleague", "cursor"));
        Assert.NotNull(await client.GetAsync(id, "Colleague", "cursor"));

        Assert.Equal(2, queries.Count);
        Assert.All(queries, query =>
        {
            Assert.True(query.Disposed);
            Assert.Equal("trusted-actor", query.Actor!.ActorId);
            Assert.Equal(id, query.CaseId);
            Assert.Equal("Colleague", query.Search);
            Assert.Equal("cursor", query.After);
            Assert.Equal(25, query.Limit);
        });
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Get_RejectsAnonymousOrMissingActorBeforeResolvingQuery(bool authenticated, bool actorClaim)
    {
        await using var services = new ServiceCollection()
            .AddScoped<ICaseCollaboratorCandidatesQuery>(_ => throw new InvalidOperationException("Must not resolve"))
            .BuildServiceProvider();
        var client = new CaseCollaboratorCandidatesGatewayClient(new AuthProvider(authenticated, actorClaim), services);
        Assert.Null(await client.GetAsync(Guid.NewGuid(), null, null));
    }

    private sealed class AuthProvider(bool authenticated, bool actorClaim = true) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var claims = new List<Claim> { new(ActorContext.AccessEpochClaimType, "0") };
            if (actorClaim) claims.Add(new(ClaimTypes.NameIdentifier, "trusted-actor"));
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "test" : null))));
        }
    }

    private sealed class Query : ICaseCollaboratorCandidatesQuery, IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ActorContext? Actor { get; private set; }
        public Guid CaseId { get; private set; }
        public string? Search { get; private set; }
        public string? After { get; private set; }
        public int Limit { get; private set; }

        public Task<CaseCollaboratorCandidatePage?> GetAsync(ActorContext actor, Guid caseId, string? search, string? after, int limit, CancellationToken cancellationToken)
        {
            (Actor, CaseId, Search, After, Limit) = (actor, caseId, search, after, limit);
            return Task.FromResult<CaseCollaboratorCandidatePage?>(new([], null));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
