using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Integration.Tests;

public sealed class FollowUpReviewGatewayClientTests
{
    [Fact]
    public async Task ConcurrentReviewsUseSeparateDisposedScopesAndTrustedActor()
    {
        var coordinator = new Coordinator();
        await using var services = new ServiceCollection()
            .AddSingleton(coordinator)
            .AddScoped<IFollowUpRecommendationReviewService, ScopedReview>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var client = new FollowUpReviewGatewayClient(new FixtureAuthentication(), services);
        var results = await Task.WhenAll(
            client.ReviewAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            client.ReviewAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, result => Assert.Equal(FollowUpReviewStatus.Recorded, result.Status));
        Assert.Equal(2, coordinator.ServiceIds.Distinct().Count());
        Assert.Equal(2, coordinator.Disposed);
        Assert.All(coordinator.Actors, actor => { Assert.Equal("synthetic-officer", actor.ActorId); Assert.Equal(7, actor.AccessEpoch); });
    }

    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "synthetic-officer"),
                new Claim(ActorContext.AccessEpochClaimType, "7")], "fixture"))));
    }

    private sealed class Coordinator
    {
        public ConcurrentBag<Guid> ServiceIds { get; } = [];
        public ConcurrentBag<ActorContext> Actors { get; } = [];
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public int Disposed;
    }

    private sealed class ScopedReview(Coordinator coordinator) : IFollowUpRecommendationReviewService, IAsyncDisposable
    {
        private readonly Guid _id = Guid.NewGuid();
        public async Task<FollowUpRecommendationReviewOutcome> ReviewAsync(ActorContext actor, Guid caseId, Guid recommendationId, Guid commandId, CancellationToken ct)
        {
            coordinator.ServiceIds.Add(_id);
            coordinator.Actors.Add(actor);
            if (Interlocked.Increment(ref coordinator.Calls) == 2) coordinator.Ready.TrySetResult();
            await coordinator.Ready.Task.WaitAsync(ct);
            return FollowUpRecommendationReviewOutcome.Recorded;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref coordinator.Disposed);
            return ValueTask.CompletedTask;
        }
    }
}
