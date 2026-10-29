using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Intake;
using SwiftAid.Gateway.Intake;

namespace SwiftAid.Integration.Tests;

public sealed class GatewayIntakeReviewClientTests
{
    private static readonly Guid ReceiptId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task ReviewAsync_AcceptIntoCase_MapsPromotionResult()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IIntakeService>(new StubIntakeService());
        var client = new GatewayIntakeReviewClient(
            new StubAuthenticationStateProvider(AuthenticatedPrincipal()),
            services.BuildServiceProvider());
        var command = new IntakeReviewCommand(
            ReceiptId,
            ExpectedVersion: 3,
            IntakeReviewDecision.AcceptIntoCase);

        var result = await client.ReviewAsync(command);

        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        Assert.NotNull(result.Value);
        Assert.Equal(ReceiptId, result.Value.ReceiptId);
        Assert.NotNull(result.Value.PromotedCaseId);
    }

    [Fact]
    public async Task ReviewAsync_InvalidCommand_IsRejectedWithoutServiceCall()
    {
        var service = new StubIntakeService();
        var services = new ServiceCollection();
        services.AddSingleton<IIntakeService>(service);
        var client = new GatewayIntakeReviewClient(
            new StubAuthenticationStateProvider(AuthenticatedPrincipal()),
            services.BuildServiceProvider());
        var command = new IntakeReviewCommand(Guid.Empty, ExpectedVersion: 0, IntakeReviewDecision.Decline);

        var result = await client.ReviewAsync(command);

        Assert.Equal(UiCapabilityResultCode.InvalidRequest, result.Code);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task ReviewAsync_Unauthenticated_IsDenied()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IIntakeService>(new StubIntakeService());
        var client = new GatewayIntakeReviewClient(
            new StubAuthenticationStateProvider(new ClaimsPrincipal()),
            services.BuildServiceProvider());
        var command = new IntakeReviewCommand(
            ReceiptId,
            ExpectedVersion: 3,
            IntakeReviewDecision.Decline,
            ReviewReason: "Duplicate report.");

        var result = await client.ReviewAsync(command);

        Assert.Equal(UiCapabilityResultCode.Denied, result.Code);
    }

    [Fact]
    public async Task ListAsync_StaysUnavailableWithoutReviewerQueue()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IIntakeService>(new StubIntakeService());
        var client = new GatewayIntakeReviewClient(
            new StubAuthenticationStateProvider(AuthenticatedPrincipal()),
            services.BuildServiceProvider());

        var result = await client.ListAsync(new IntakeReviewQuery(new PageRequest(1, 25)));

        Assert.Equal(UiCapabilityResultCode.Unavailable, result.Code);
    }

    private static ClaimsPrincipal AuthenticatedPrincipal() => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "reviewer-1"),
            new Claim(ActorContext.AccessEpochClaimType, "4"),
        ],
        authenticationType: "test"));

    private sealed class StubAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class StubIntakeService : IIntakeService
    {
        public int Calls { get; private set; }

        public Task<IntakeDecisionResult> DecideAsync(IntakeDecision command, ActorContext actor, CancellationToken ct)
        {
            Calls++;
            var receipt = new IntakeReceipt(
                command.ReceiptId,
                Guid.NewGuid(),
                IntakeStatus.AcceptedIntoCase,
                IntakeRoutingStatus.RoutedForReview,
                DateTimeOffset.UtcNow);
            return Task.FromResult(new IntakeDecisionResult(
                IntakeDecisionStatus.Applied,
                receipt,
                PromotedCaseId: Guid.Parse("55555555-5555-5555-5555-555555555555")));
        }

        public Task<IntakeReceipt> SubmitAsync(IntakeSubmission command, IntakeCapability capability, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
