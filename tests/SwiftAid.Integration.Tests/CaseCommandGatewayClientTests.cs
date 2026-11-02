using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Integration.Tests;

public sealed class CaseCommandGatewayClientTests
{
    [Theory]
    [InlineData("GrantCaseCollaborator")]
    [InlineData("RevokeCaseCollaborator")]
    public async Task Collaborator_commands_preserve_grant_identity_and_retry_version(string commandType)
    {
        var processor = new TestCaseCommandProcessor();
        using var services = CreateServices(processor, new TestCaseTimelineQuery(FixedCaseId));
        var client = new CaseCommandGatewayClient(new TestAuthenticationStateProvider(), services);
        var input = new CaseCommandInput(commandType, null, "Synthetic reason", null, null, null, 2,
            CommandId: Guid.NewGuid(), GrantId: Guid.NewGuid(), CollaboratorActorId: "recipient",
            CollaborationActions: CaseCollaborationActions.ReadCase | CaseCollaborationActions.ManageFollowUp,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        var result = await client.SendAsync(FixedCaseId, input);
        Assert.Equal(CaseCommandStatus.Accepted, result.Status);
        Assert.Equal(2, processor.LastCommand!.ExpectedVersion);
        Assert.Equal(input.CommandId, processor.LastCommand.CommandId);
        if (commandType == "GrantCaseCollaborator")
        {
            var grant = Assert.IsType<GrantCaseCollaboratorCommand>(processor.LastCommand);
            Assert.Equal(input.GrantId, grant.GrantId);
            Assert.Equal(input.CollaboratorActorId, grant.CollaboratorActorId);
            Assert.Equal(input.CollaborationActions, grant.Actions);
            Assert.Equal(input.ExpiresAtUtc, grant.ExpiresAtUtc);
        }
        else
        {
            Assert.Equal(input.GrantId, Assert.IsType<RevokeCaseCollaboratorCommand>(processor.LastCommand).GrantId);
        }

        Assert.Equal(CaseCommandStatus.Invalid, (await client.SendAsync(FixedCaseId, input with { GrantId = null })).Status);
        Assert.Equal(1, processor.Calls);
    }

    [Fact]
    public async Task Assignment_acknowledgement_preserves_assignment_and_retry_versions()
    {
        var processor = new TestCaseCommandProcessor();
        using var services = CreateServices(processor, new TestCaseTimelineQuery(FixedCaseId));
        var client = new CaseCommandGatewayClient(new TestAuthenticationStateProvider(), services);
        var input = new CaseCommandInput("AcknowledgeAssignment", null, null, null, null, null, 2,
            CommandId: Guid.NewGuid(), AssignmentVersion: 2);
        var result = await client.SendAsync(FixedCaseId, input);
        Assert.Equal(CaseCommandStatus.Accepted, result.Status);
        var command = Assert.IsType<AcknowledgeAssignmentCommand>(processor.LastCommand);
        Assert.Equal(input.CommandId, command.CommandId);
        Assert.Equal(2, command.ExpectedVersion);
        Assert.Equal(2, command.AssignmentVersion);
        var invalid = await client.SendAsync(FixedCaseId, input with { AssignmentVersion = null });
        Assert.Equal(CaseCommandStatus.Invalid, invalid.Status);
        Assert.Equal(1, processor.Calls);
    }

    private static readonly Guid FixedCaseId = Guid.Parse("c0de0001-1111-4c22-9dd9-123456789abc");

    [Fact]
    public async Task Send_UsesSuppliedCommandId_AndRejectsEmptyId()
    {
        var processor = new TestCaseCommandProcessor();
        using var services = CreateServices(processor, new TestCaseTimelineQuery(FixedCaseId));
        var client = new CaseCommandGatewayClient(new TestAuthenticationStateProvider(), services);
        var commandId = Guid.NewGuid();
        var result = await client.SendAsync(FixedCaseId,
            new CaseCommandInput("Resolve", null, "done", null, null, null, 3, CommandId: commandId));
        Assert.Equal(commandId, result.CommandId);
        Assert.Equal(commandId, processor.LastCommand!.CommandId);

        var invalid = await client.SendAsync(FixedCaseId,
            new CaseCommandInput("Resolve", null, "done", null, null, null, 3, CommandId: Guid.Empty));
        Assert.Equal(CaseCommandStatus.Invalid, invalid.Status);
        Assert.Equal(1, processor.Calls);
    }

    [Fact]
    public async Task CorrectReport_MapsNarrativeAndReasonToSeparateCommandFields()
    {
        var processor = new TestCaseCommandProcessor();
        var timeline = new TestCaseTimelineQuery(FixedCaseId);
        using var services = CreateServices(processor, timeline);
        var client = new CaseCommandGatewayClient(
            new TestAuthenticationStateProvider(),
            services);
        var input = new CaseCommandInput(
            "CorrectReport",
            Action: null,
            Reason: "reason for correction",
            ProposedOwnerId: null,
            RequestId: null,
            Outcome: null,
            ExpectedVersion: 3,
            Narrative: "corrected report text");

        var result = await client.SendAsync(FixedCaseId, input);

        Assert.Equal(CaseCommandStatus.Accepted, result.Status);
        var command = Assert.IsType<CorrectReportCommand>(processor.LastCommand);
        Assert.NotEqual(Guid.Empty, command.CommandId);
        Assert.Equal(command.CommandId, result.CommandId);
        Assert.Equal("corrected report text", command.CorrectedNarrative);
        Assert.Equal("reason for correction", command.Reason);
    }

    [Fact]
    public async Task CorrectReport_PreservesStaleExpectedVersionAndReturnsConflict()
    {
        var processor = new TestCaseCommandProcessor { AuthoritativeVersion = 5 };
        var timeline = new TestCaseTimelineQuery(FixedCaseId);
        using var services = CreateServices(processor, timeline);
        var client = new CaseCommandGatewayClient(new TestAuthenticationStateProvider(), services);

        var result = await client.SendAsync(FixedCaseId, new CaseCommandInput(
            "CorrectReport", null, "correction reason", null, null, null,
            ExpectedVersion: 4, Narrative: "corrected report"));

        Assert.Equal(CaseCommandStatus.Conflict, result.Status);
        Assert.Equal(1, timeline.Calls);
        Assert.Equal(5, timeline.LatestVersion);
        Assert.Equal(4, processor.LastCommand!.ExpectedVersion);
    }

    [Theory]
    [InlineData(null, "reason for correction")]
    [InlineData("corrected report text", null)]
    public async Task CorrectReport_RequiresNarrativeAndReason(string? narrative, string? reason)
    {
        var processor = new TestCaseCommandProcessor();
        var timeline = new TestCaseTimelineQuery(FixedCaseId);
        using var services = CreateServices(processor, timeline);
        var client = new CaseCommandGatewayClient(
            new TestAuthenticationStateProvider(),
            services);
        var input = new CaseCommandInput(
            "CorrectReport",
            Action: null,
            Reason: reason,
            ProposedOwnerId: null,
            RequestId: null,
            Outcome: null,
            ExpectedVersion: 3,
            Narrative: narrative);

        var result = await client.SendAsync(FixedCaseId, input);

        Assert.Equal(CaseCommandStatus.Invalid, result.Status);
        Assert.Equal(0, processor.Calls);
    }

    private static ServiceProvider CreateServices(
        TestCaseCommandProcessor processor,
        TestCaseTimelineQuery timeline)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICaseCommandProcessor>(processor);
        services.AddSingleton<ICaseTimelineQuery>(timeline);
        return services.BuildServiceProvider();
    }

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        private static readonly ClaimsPrincipal AuthenticatedPrincipal = new(
            new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "synthetic-staff"),
                new Claim(ActorContext.AccessEpochClaimType, "0")
            ],
            "Synthetic"));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(AuthenticatedPrincipal));
    }

    private sealed class TestCaseTimelineQuery(Guid caseId) : ICaseTimelineQuery
    {
        public int Calls { get; private set; }

        public long LatestVersion => 5;

        public Task<CaseTimelineResult> GetAsync(
            ActorContext actor,
            Guid requestedCaseId,
            int? limit,
            CancellationToken ct,
            long? beforeAggregateVersion = null)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            Assert.Equal("synthetic-staff", actor.ActorId);
            Assert.Equal(caseId, requestedCaseId);

            var header = new CaseTimelineHeader(
                caseId,
                "synthetic-organization",
                "synthetic-workspace",
                "ZW",
                "Human",
                "synthetic-staff",
                5,
                1,
                DateTimeOffset.UnixEpoch);
            var timeline = new CaseTimeline(header, []);
            return Task.FromResult(new CaseTimelineResult(CaseTimelineAccess.Found, timeline));
        }
    }

    private sealed class TestCaseCommandProcessor : ICaseCommandProcessor
    {
        public long? AuthoritativeVersion { get; init; }

        public int Calls { get; private set; }

        public CaseCommand? LastCommand { get; private set; }

        public ValueTask<CaseCommandProcessingResult> ProcessAsync(
            ActorContext actor,
            Guid caseId,
            int wireCommandSchemaVersion,
            CaseCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastCommand = command;

            return ValueTask.FromResult(new CaseCommandProcessingResult(
                AuthoritativeVersion is null || command.ExpectedVersion == AuthoritativeVersion
                    ? CaseCommandProcessingStatus.Accepted
                    : CaseCommandProcessingStatus.Conflict,
                caseId,
                command.CommandId,
                ImmutableArray.Create(Guid.NewGuid()),
                command.ExpectedVersion,
                2));
        }
    }
}
