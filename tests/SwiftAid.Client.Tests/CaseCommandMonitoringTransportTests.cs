using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Client.Cases;
using SwiftAid.Client.Outbox;
using SwiftAid.Client.Sync;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Client.Tests;

/// <summary>
/// P5.2 monitoring commands: the extended <see cref="CaseCommandInput"/> fields
/// round-trip through the intent codec, the HTTP client, and the outbox transport.
/// </summary>
public sealed class CaseCommandMonitoringTransportTests
{
    [Fact]
    public async Task Collaborator_scope_roundtrips_and_direct_http_preserves_identity()
    {
        var caseId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var input = new CaseCommandInput("GrantCaseCollaborator", null, "Synthetic grant", null, null, null, 4,
            CommandId: commandId, GrantId: Guid.NewGuid(), CollaboratorActorId: "recipient",
            CollaborationActions: CaseCollaborationActions.ReadCase | CaseCollaborationActions.ReadAttachment,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        var bytes = CaseCommandIntentCodec.Encode(CaseCommandIntentCodec.FromInput(input));
        var decoded = CaseCommandIntentCodec.Decode(bytes);
        Assert.Equal(bytes, CaseCommandIntentCodec.Encode(decoded));
        Assert.Equal(input.GrantId, decoded.GrantId);
        Assert.Equal(input.CollaboratorActorId, decoded.CollaboratorActorId);
        Assert.Equal(input.CollaborationActions, decoded.CollaborationActions);
        Assert.Equal(input.ExpiresAtUtc, decoded.ExpiresAtUtc);
        var legacy = CaseCommandIntentCodec.Encode(new CaseCommandIntent("Resolve", null, "Synthetic", null, null, null));
        using var oldBody = JsonDocument.Parse(legacy);
        foreach (var field in new[] { "grantId", "collaboratorActorId", "collaborationActions", "expiresAtUtc" })
        {
            Assert.False(oldBody.RootElement.TryGetProperty(field, out _));
        }

        var handler = new ScriptedHandler([JsonResponse(HttpStatusCode.OK, new
        {
            caseId, commandId, eventIds = new[] { Guid.NewGuid() }, aggregateVersion = 5, reportRevision = 1
        })]);
        var client = new CaseCommandHttpClient(new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });
        Assert.Equal(CaseCommandStatus.Accepted, (await client.SendAsync(caseId, input)).Status);
        using var posted = JsonDocument.Parse(await Assert.Single(handler.Requests).Content!.ReadAsStringAsync());
        var root = posted.RootElement;
        Assert.Equal(commandId, root.GetProperty("commandId").GetGuid());
        Assert.Equal(4, root.GetProperty("expectedVersion").GetInt64());
        Assert.Equal(input.GrantId, root.GetProperty("grantId").GetGuid());
        Assert.Equal(input.CollaboratorActorId, root.GetProperty("collaboratorActorId").GetString());
        Assert.Equal((int)input.CollaborationActions!.Value, root.GetProperty("collaborationActions").GetInt32());
        Assert.Equal(input.ExpiresAtUtc, root.GetProperty("expiresAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public void Assignment_version_roundtrips_without_adding_a_field_to_older_intents()
    {
        var input = new CaseCommandInput("AcknowledgeAssignment", null, null, null, null, null, 5, AssignmentVersion: 2);
        var bytes = CaseCommandIntentCodec.Encode(CaseCommandIntentCodec.FromInput(input));
        var decoded = CaseCommandIntentCodec.Decode(bytes);
        Assert.Equal(2, decoded.AssignmentVersion);
        Assert.Equal(bytes, CaseCommandIntentCodec.Encode(decoded));
        var legacy = CaseCommandIntentCodec.Encode(CaseCommandIntentCodec.FromInput(input with { CommandType = "Resolve", AssignmentVersion = null }));
        using var json = JsonDocument.Parse(legacy);
        Assert.False(json.RootElement.TryGetProperty("assignmentVersion", out _));
        Assert.Null(CaseCommandIntentCodec.Decode(legacy).AssignmentVersion);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void IntentCodec_RoundTripsExtendedFields()
    {
        var input = new CaseCommandInput(
            "ScheduleFollowUp", null, "revisit holding.", null, null, null, 5,
            ObservationId: Guid.NewGuid(),
            Narrative: "subject resting",
            Source: "home-visit",
            FollowUpId: Guid.NewGuid(),
            Purpose: "revisit holding",
            AssigneeActorId: "worker-1",
            DueAtUtc: new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero),
            OutcomeName: "Rescheduled",
            OutcomeNotes: "retry after market day",
            NewDueAtUtc: new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero),
            EscalationId: Guid.NewGuid(),
            RecipientRoute: "duty-officer",
            Resolution: "supervisor visited holding",
            ActionSummary: "supervisor paged",
            EvidenceEventIds: ["evidence-1", "evidence-2"]);

        var decoded = CaseCommandIntentCodec.Decode(
            CaseCommandIntentCodec.Encode(CaseCommandIntentCodec.FromInput(input)));

        Assert.Equal(input.CommandType, decoded.CommandType);
        Assert.Equal(input.ObservationId, decoded.ObservationId);
        Assert.Equal(input.Narrative, decoded.Narrative);
        Assert.Equal(input.Source, decoded.Source);
        Assert.Equal(input.FollowUpId, decoded.FollowUpId);
        Assert.Equal(input.Purpose, decoded.Purpose);
        Assert.Equal(input.AssigneeActorId, decoded.AssigneeActorId);
        Assert.Equal(input.DueAtUtc, decoded.DueAtUtc);
        Assert.Equal(input.OutcomeName, decoded.OutcomeName);
        Assert.Equal(input.OutcomeNotes, decoded.OutcomeNotes);
        Assert.Equal(input.NewDueAtUtc, decoded.NewDueAtUtc);
        Assert.Equal(input.EscalationId, decoded.EscalationId);
        Assert.Equal(input.RecipientRoute, decoded.RecipientRoute);
        Assert.Equal(input.Resolution, decoded.Resolution);
        Assert.Equal(input.ActionSummary, decoded.ActionSummary);
        Assert.Equal(input.EvidenceEventIds, decoded.EvidenceEventIds);
    }

    [Fact]
    public void IntentCodec_DecodesLegacyPayloadWithNullExtensions()
    {
        var legacy = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            commandType = "Resolve",
            action = (string?)null,
            reason = "Resolved.",
            proposedOwnerId = (string?)null,
            requestId = (Guid?)null,
            outcome = (string?)null,
        }, JsonOptions);

        var decoded = CaseCommandIntentCodec.Decode(legacy);

        Assert.Equal("Resolve", decoded.CommandType);
        Assert.Equal("Resolved.", decoded.Reason);
        Assert.Null(decoded.ObservationId);
        Assert.Null(decoded.FollowUpId);
        Assert.Null(decoded.EscalationId);
        Assert.Null(decoded.OutcomeName);
        Assert.Null(decoded.EvidenceEventIds);
    }

    [Fact]
    public async Task HttpClient_PostsExtendedFields()
    {
        var caseId = Guid.NewGuid();
        var observationId = Guid.NewGuid();
        var dueAt = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
        var receiptCommandId = Guid.NewGuid();
        var handler = new ScriptedHandler([
            JsonResponse(HttpStatusCode.OK, new
            {
                caseId,
                commandId = receiptCommandId,
                eventIds = new[] { Guid.NewGuid() },
                aggregateVersion = 8,
                reportRevision = 1,
            }),
        ]);
        var client = new CaseCommandHttpClient(new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });
        var input = new CaseCommandInput(
            "RecordObservation", null, "noted during visit.", null, null, null, 7,
            ObservationId: observationId, Narrative: "subject resting", Source: "home-visit",
            FollowUpId: Guid.NewGuid(), Purpose: "revisit holding", AssigneeActorId: "worker-1", DueAtUtc: dueAt,
            OutcomeName: "Missed", OutcomeNotes: "nobody present",
            EscalationId: Guid.NewGuid(), RecipientRoute: "duty-officer", Resolution: "resolved",
            ActionSummary: "summary", EvidenceEventIds: ["evidence-1"]);

        var result = await client.SendAsync(caseId, input);

        Assert.Equal(CaseCommandStatus.Accepted, result.Status);
        var post = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(await post.Content!.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal("RecordObservation", root.GetProperty("commandType").GetString());
        Assert.Equal(observationId, root.GetProperty("observationId").GetGuid());
        Assert.Equal("subject resting", root.GetProperty("narrative").GetString());
        Assert.Equal("home-visit", root.GetProperty("source").GetString());
        Assert.Equal(input.FollowUpId!.Value, root.GetProperty("followUpId").GetGuid());
        Assert.Equal("revisit holding", root.GetProperty("purpose").GetString());
        Assert.Equal("worker-1", root.GetProperty("assigneeActorId").GetString());
        Assert.Equal(dueAt, root.GetProperty("dueAtUtc").GetDateTimeOffset());
        Assert.Equal("Missed", root.GetProperty("outcomeName").GetString());
        Assert.Equal("nobody present", root.GetProperty("outcomeNotes").GetString());
        Assert.Equal(input.EscalationId!.Value, root.GetProperty("escalationId").GetGuid());
        Assert.Equal("duty-officer", root.GetProperty("recipientRoute").GetString());
        Assert.Equal("resolved", root.GetProperty("resolution").GetString());
        Assert.Equal("summary", root.GetProperty("actionSummary").GetString());
        Assert.Equal("evidence-1", Assert.Single(root.GetProperty("evidenceEventIds").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task OutboxTransport_PostsExtendedFields()
    {
        var caseId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var escalationId = Guid.NewGuid();
        var input = new CaseCommandInput(
            "RaiseEscalation", null, "holding unreachable twice.", null, null, null, 5,
            EscalationId: escalationId, RecipientRoute: "duty-officer", EvidenceEventIds: ["evidence-1", "evidence-2"]);
        var intent = CaseCommandIntentCodec.FromInput(input);
        var handler = new ScriptedHandler([
            JsonResponse(HttpStatusCode.OK, new
            {
                caseId,
                commandId,
                eventIds = new[] { Guid.NewGuid() },
                aggregateVersion = 7,
                reportRevision = 1,
            }),
        ]);
        var transport = new CaseCommandOutboxTransport(
            new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        var result = await transport.SendAsync(
            new OutboundCaseCommand(
                commandId, caseId, "actor-a", 1, "workspace-a", input.CommandType, 5,
                CaseCommandIntentCodec.Encode(intent), [], 0),
            CancellationToken.None);

        Assert.Equal(CaseCommandDeliveryStatus.Accepted, result.Status);
        var post = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(await post.Content!.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal("RaiseEscalation", root.GetProperty("commandType").GetString());
        Assert.Equal(escalationId, root.GetProperty("escalationId").GetGuid());
        Assert.Equal("duty-officer", root.GetProperty("recipientRoute").GetString());
        Assert.Equal(
            new[] { "evidence-1", "evidence-2" },
            root.GetProperty("evidenceEventIds").EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T value) =>
        new(status) { Content = JsonContent.Create(value, options: JsonOptions) };

    private sealed class ScriptedHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
