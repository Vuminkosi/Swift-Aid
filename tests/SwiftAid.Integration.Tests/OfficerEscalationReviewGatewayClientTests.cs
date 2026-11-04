using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Integration.Tests;

public sealed class OfficerEscalationReviewGatewayClientTests
{
    [Fact]
    public async Task HumanSelectionsAreBoundToAuthenticatedActorAndUnknownResultsRemainRetryable()
    {
        var approval = new ApprovalService();
        var assignment = new AssignmentService();
        await using var services = new ServiceCollection().AddSingleton<IHealthOfficerEscalationApprovalService>(approval)
            .AddSingleton<IHealthOfficerEscalationAssignmentService>(assignment)
            .AddSingleton<IHealthOfficerEscalationReviewQuery, ReviewQuery>().BuildServiceProvider();
        var client = new OfficerEscalationReviewGatewayClient(new Auth(true), services);
        var selection = new OfficerEscalationReviewedSelection(Guid.NewGuid(), 8, Guid.NewGuid(), "officer", Guid.NewGuid(), 3,
            Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var context = await client.GetAsync(selection.CaseId);
        Assert.Equal(8, context!.CaseVersion);
        Assert.Equal("officer", Assert.Single(context.Officers).ActorId);
        Assert.Equal(OfficerEscalationActionStatus.Recorded, (await client.ApproveAsync(selection)).Status);
        Assert.Equal("reviewer", approval.Actor!.ActorId);
        Assert.Equal(selection.DesignationId, approval.Request!.DesignationId);
        Assert.Equal(selection.ApprovalCommandId, approval.Request.CommandId);
        Assert.Equal(selection.AcknowledgementDueUtc, approval.Request.AcknowledgementDueUtc);
        var assigned = await client.AssignAsync(selection);
        Assert.Equal(OfficerEscalationActionStatus.Recorded, assigned.Status);
        Assert.Equal(selection.ApprovalId, assignment.Request!.ApprovalId);
        Assert.Equal(selection.AssignmentCommandId, assignment.Request.CommandId);
        Assert.Equal("reviewer", assignment.Actor!.ActorId);
        assignment.Throw = true;
        Assert.Equal(OfficerEscalationActionStatus.OutcomeUnknown, (await client.AssignAsync(selection)).Status);
        var denied = new OfficerEscalationReviewGatewayClient(new Auth(false), services);
        Assert.Equal(OfficerEscalationActionStatus.Denied, (await denied.ApproveAsync(selection)).Status);
    }

    private sealed class Auth(bool signedIn) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "reviewer"), new Claim(ActorContext.AccessEpochClaimType, "2")], signedIn ? "fixture" : null))));
    }
    private sealed class ApprovalService : IHealthOfficerEscalationApprovalService
    {
        public ActorContext? Actor { get; private set; }
        public HealthOfficerEscalationApprovalRequest? Request { get; private set; }
        public Task<HealthOfficerEscalationApprovalResult> ApproveAsync(ActorContext actor, HealthOfficerEscalationApprovalRequest request, CancellationToken ct)
        { Actor = actor; Request = request; return Task.FromResult(new HealthOfficerEscalationApprovalResult(HealthOfficerEscalationApprovalStatus.Recorded, request.ApprovalId)); }
    }
    private sealed class AssignmentService : IHealthOfficerEscalationAssignmentService
    {
        public ActorContext? Actor { get; private set; }
        public HealthOfficerEscalationAssignmentRequest? Request { get; private set; }
        public bool Throw { get; set; }
        public Task<HealthOfficerEscalationAssignmentResult> AssignAsync(ActorContext actor, HealthOfficerEscalationAssignmentRequest request, CancellationToken ct)
        {
            Actor = actor; Request = request;
            if (Throw) throw new InvalidOperationException("Synthetic unknown outcome");
            return Task.FromResult(new HealthOfficerEscalationAssignmentResult(HealthOfficerEscalationAssignmentStatus.Recorded, Guid.NewGuid(), 9));
        }
    }
    private sealed class ReviewQuery : IHealthOfficerEscalationReviewQuery
    {
        public Task<HealthOfficerEscalationReviewContext?> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct)
        {
            Assert.Equal("reviewer", actor.ActorId);
            return Task.FromResult<HealthOfficerEscalationReviewContext?>(new(8, [new(Guid.NewGuid(), DateTimeOffset.UtcNow)], false,
                [new(Guid.NewGuid(), 3, "officer", "Officer")], false));
        }
    }
}
