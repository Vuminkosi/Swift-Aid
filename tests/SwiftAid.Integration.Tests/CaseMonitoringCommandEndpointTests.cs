using System.Net;
using System.Text;
using System.Text.Json;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// P5.2 monitoring commands: mapping, 400s, and denial mapping for the six
/// case-monitoring commands. Fake-processor style, no live DB.
/// </summary>
public sealed class CaseMonitoringCommandEndpointTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(2L)]
    public async Task Assignment_acknowledgement_requires_an_explicit_positive_assignment_version(long? assignmentVersion)
    {
        await using var host = await TestCommandHost.StartAsync();
        var commandId = Guid.NewGuid();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "AcknowledgeAssignment", commandId, 3, null, null, null, null, null, null,
            AssignmentVersion: assignmentVersion));
        using var response = await host.SendBearerAsync(request);
        Assert.Equal(assignmentVersion > 0 ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        if (assignmentVersion > 0)
        {
            var command = Assert.IsType<AcknowledgeAssignmentCommand>(host.Processor.LastCommand);
            Assert.Equal(assignmentVersion, command.AssignmentVersion);
            Assert.Equal(commandId, command.CommandId);
        }
        else
        {
            Assert.Equal(0, host.Processor.Calls);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid FixedCaseId = Guid.Parse("c0de0001-1111-4c22-9dd9-123456789abc");

    [Fact]
    public async Task RecordObservation_IsAccepted()
    {
        await using var host = await TestCommandHost.StartAsync();
        var commandId = Guid.NewGuid();
        var observationId = Guid.NewGuid();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RecordObservation", commandId, 3, null, "noted during visit", null, null, null, null,
            ObservationId: observationId, Narrative: "subject resting, breathing normal", Source: "home-visit"));

        using var response = await host.SendBearerAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JsonSerializer.Deserialize<CaseCommandApiResponse>(body, JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(commandId, receipt!.CommandId);
        var command = Assert.IsType<RecordObservationCommand>(host.Processor.LastCommand);
        Assert.Equal(observationId, command.ObservationId);
        Assert.Equal("subject resting, breathing normal", command.Narrative);
        Assert.Equal("home-visit", command.Source);
        Assert.Equal("noted during visit", command.Reason);
    }

    [Fact]
    public async Task RecordObservation_WithoutNarrative_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RecordObservation", Guid.NewGuid(), 3, null, "noted", null, null, null, null,
            ObservationId: Guid.NewGuid(), Narrative: " ", Source: "home-visit"));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task RecordObservation_WithoutObservationId_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RecordObservation", Guid.NewGuid(), 3, null, "noted", null, null, null, null,
            Narrative: "subject resting", Source: "home-visit"));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task RecordObservation_WithoutReason_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RecordObservation", Guid.NewGuid(), 3, null, " ", null, null, null, null,
            ObservationId: Guid.NewGuid(), Narrative: "subject resting", Source: "home-visit"));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task ScheduleFollowUp_IsAccepted()
    {
        await using var host = await TestCommandHost.StartAsync();
        var commandId = Guid.NewGuid();
        var followUpId = Guid.NewGuid();
        var dueAt = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "ScheduleFollowUp", commandId, 3, null, "revisit holding", null, null, null, null,
            FollowUpId: followUpId, Purpose: "revisit holding", AssigneeActorId: "worker-1", DueAtUtc: dueAt));

        using var response = await host.SendBearerAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JsonSerializer.Deserialize<CaseCommandApiResponse>(body, JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(commandId, receipt!.CommandId);
        var command = Assert.IsType<ScheduleFollowUpCommand>(host.Processor.LastCommand);
        Assert.Equal(followUpId, command.FollowUpId);
        Assert.Equal("revisit holding", command.Purpose);
        Assert.Equal("worker-1", command.AssigneeActorId);
        Assert.Equal(dueAt, command.DueAtUtc);
    }

    [Fact]
    public async Task ScheduleFollowUp_WithoutDueAtUtc_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "ScheduleFollowUp", Guid.NewGuid(), 3, null, "revisit", null, null, null, null,
            FollowUpId: Guid.NewGuid(), Purpose: "revisit holding", AssigneeActorId: "worker-1"));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task ScheduleFollowUp_WithoutAssignee_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "ScheduleFollowUp", Guid.NewGuid(), 3, null, "revisit", null, null, null, null,
            FollowUpId: Guid.NewGuid(), Purpose: "revisit holding",
            DueAtUtc: new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero)));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task RecordFollowUpOutcome_IsAccepted()
    {
        await using var host = await TestCommandHost.StartAsync();
        var commandId = Guid.NewGuid();
        var followUpId = Guid.NewGuid();
        var newDueAt = new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RecordFollowUpOutcome", commandId, 3, null, "retry after market day", null, null, null, null,
            FollowUpId: followUpId, OutcomeName: "Rescheduled", OutcomeNotes: "retry after market day", NewDueAtUtc: newDueAt));

        using var response = await host.SendBearerAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JsonSerializer.Deserialize<CaseCommandApiResponse>(body, JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(commandId, receipt!.CommandId);
        var command = Assert.IsType<RecordFollowUpOutcomeCommand>(host.Processor.LastCommand);
        Assert.Equal(followUpId, command.FollowUpId);
        Assert.Equal(FollowUpOutcome.Rescheduled, command.Outcome);
        Assert.Equal("retry after market day", command.OutcomeNotes);
        Assert.Equal(newDueAt, command.NewDueAtUtc);
    }

    [Fact]
    public async Task RecordFollowUpOutcome_WithUnknownOutcome_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RecordFollowUpOutcome", Guid.NewGuid(), 3, null, "notes", null, null, null, null,
            FollowUpId: Guid.NewGuid(), OutcomeName: "Maybe", OutcomeNotes: "notes"));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task RecordFollowUpOutcome_WithoutFollowUpId_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RecordFollowUpOutcome", Guid.NewGuid(), 3, null, "notes", null, null, null, null,
            OutcomeName: "Missed", OutcomeNotes: "nobody present"));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task RaiseEscalation_IsAccepted()
    {
        await using var host = await TestCommandHost.StartAsync();
        var commandId = Guid.NewGuid();
        var escalationId = Guid.NewGuid();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RaiseEscalation", commandId, 3, null, "holding unreachable twice", null, null, null, null,
            EscalationId: escalationId, RecipientRoute: "duty-officer", EvidenceEventIds: ["evidence-1"]));

        using var response = await host.SendBearerAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JsonSerializer.Deserialize<CaseCommandApiResponse>(body, JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(commandId, receipt!.CommandId);
        var command = Assert.IsType<RaiseEscalationCommand>(host.Processor.LastCommand);
        Assert.Equal(escalationId, command.EscalationId);
        Assert.Equal("duty-officer", command.RecipientRoute);
        Assert.Equal(["evidence-1"], command.EvidenceEventIds);
    }

    [Fact]
    public async Task RaiseEscalation_WithoutRecipientRoute_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RaiseEscalation", Guid.NewGuid(), 3, null, "holding unreachable", null, null, null, null,
            EscalationId: Guid.NewGuid(), RecipientRoute: " "));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task RaiseEscalation_WithBlankEvidence_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RaiseEscalation", Guid.NewGuid(), 3, null, "holding unreachable", null, null, null, null,
            EscalationId: Guid.NewGuid(), RecipientRoute: "duty-officer", EvidenceEventIds: [" "]));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task AcknowledgeEscalation_IsAccepted()
    {
        await using var host = await TestCommandHost.StartAsync();
        var commandId = Guid.NewGuid();
        var escalationId = Guid.NewGuid();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "AcknowledgeEscalation", commandId, 3, null, "acknowledged", null, null, null, null,
            EscalationId: escalationId, ActionSummary: "supervisor paged"));

        using var response = await host.SendBearerAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JsonSerializer.Deserialize<CaseCommandApiResponse>(body, JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(commandId, receipt!.CommandId);
        var command = Assert.IsType<AcknowledgeEscalationCommand>(host.Processor.LastCommand);
        Assert.Equal(escalationId, command.EscalationId);
        Assert.Equal("supervisor paged", command.ActionSummary);
    }

    [Fact]
    public async Task AcknowledgeEscalation_WithoutSummary_IsAccepted()
    {
        await using var host = await TestCommandHost.StartAsync();
        var escalationId = Guid.NewGuid();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "AcknowledgeEscalation", Guid.NewGuid(), 3, null, "acknowledged", null, null, null, null,
            EscalationId: escalationId));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var command = Assert.IsType<AcknowledgeEscalationCommand>(host.Processor.LastCommand);
        Assert.Null(command.ActionSummary);
    }

    [Fact]
    public async Task AcknowledgeEscalation_WithoutEscalationId_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "AcknowledgeEscalation", Guid.NewGuid(), 3, null, "acknowledged", null, null, null, null));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task CloseEscalation_IsAccepted()
    {
        await using var host = await TestCommandHost.StartAsync();
        var commandId = Guid.NewGuid();
        var escalationId = Guid.NewGuid();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "CloseEscalation", commandId, 3, null, "resolved", null, null, null, null,
            EscalationId: escalationId, Resolution: "supervisor visited holding"));

        using var response = await host.SendBearerAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JsonSerializer.Deserialize<CaseCommandApiResponse>(body, JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(commandId, receipt!.CommandId);
        var command = Assert.IsType<CloseEscalationCommand>(host.Processor.LastCommand);
        Assert.Equal(escalationId, command.EscalationId);
        Assert.Equal("supervisor visited holding", command.Resolution);
    }

    [Fact]
    public async Task CloseEscalation_WithoutResolution_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "CloseEscalation", Guid.NewGuid(), 3, null, "resolved", null, null, null, null,
            EscalationId: Guid.NewGuid(), Resolution: " "));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task MonitoringCommand_Denied_GetsNotFound()
    {
        await using var host = await TestCommandHost.StartAsync(CaseCommandProcessingStatus.Denied);
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RecordObservation", Guid.NewGuid(), 3, null, "noted", null, null, null, null,
            ObservationId: Guid.NewGuid(), Narrative: "subject resting", Source: "home-visit"));

        using var response = await host.SendBearerAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, host.Processor.Calls);
        Assert.DoesNotContain("subject resting", body, StringComparison.Ordinal);
    }

    private static HttpRequestMessage CreateRequest(Guid caseId, CaseCommandApiRequest body) =>
        new(HttpMethod.Post, $"/api/v1/cases/{caseId:D}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
}
