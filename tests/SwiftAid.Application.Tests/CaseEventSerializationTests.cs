using System.Text;
using System.Text.Json;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Events;

namespace SwiftAid.Application.Tests;

public sealed class CaseEventSerializationTests
{
    private static readonly CaseEventSerializer Serializer = new();
    private static readonly DateTimeOffset OccurredAtUtc = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecordedAtUtc = new(2026, 10, 26, 8, 1, 0, TimeSpan.Zero);
    private static readonly Guid CaseId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid InitialCommandId = Guid.Parse("20000000-0000-0000-0000-000000000001");

    public static IEnumerable<object[]> GoldenEvents()
    {
        var report = new ReportFacts(
            CaseKind.Human,
            Sector.HumanHealth,
            "reported details",
            [new ReportedObservation("reported observation")]);
        var initialReport = new CaseReported(
            CaseId, 1, 1, InitialCommandId, "officer-1", OccurredAtUtc, RecordedAtUtc, report, "workspace-1");
        var requestId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var requestCommandId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var request = new CaseActionRequested(
            CaseId, 3, 1, requestCommandId, "worker-1", null, RecordedAtUtc, requestId,
            CaseAction.Reassign, "request transfer", ["evidence-1"], "worker-2");
        var approvedCommandId = Guid.Parse("20000000-0000-0000-0000-000000000003");
        var acceptedAssignment = new CaseAssigned(
            CaseId, 4, 1, approvedCommandId, "officer-1", null, RecordedAtUtc, "worker-2");

        yield return [initialReport, "case-reported.v1.json"];
        yield return [acceptedAssignment, "case-assigned.v1.json"];
        yield return [new CaseReportCorrected(
            CaseId, 3, 2, Guid.Parse("20000000-0000-0000-0000-000000000004"), "officer-1", null,
            RecordedAtUtc, report with { Narrative = "corrected details" }, "reporter clarification", 1),
            "case-report-corrected.v1.json"];
        yield return [request, "case-action-requested.v1.json"];
        yield return [new CaseActionRequestReviewed(
            CaseId, 5, 1, approvedCommandId, "officer-1", null, RecordedAtUtc, requestId,
            CaseActionReviewOutcome.Approved, acceptedAssignment.EventId, "authorized transfer"),
            "case-action-review-approved.v1.json"];
        yield return [new CaseActionRequestReviewed(
            CaseId, 4, 1, Guid.Parse("20000000-0000-0000-0000-000000000005"), "officer-1", null,
            RecordedAtUtc, requestId, CaseActionReviewOutcome.Denied, null, "request declined"),
            "case-action-review-denied.v1.json"];
        yield return [new CaseResolved(
            CaseId, 3, 1, Guid.Parse("20000000-0000-0000-0000-000000000006"), "officer-1", OccurredAtUtc,
            RecordedAtUtc, "administrative work complete"),
            "case-resolved.v1.json"];
        yield return [new CaseReopened(
            CaseId, 4, 1, Guid.Parse("20000000-0000-0000-0000-000000000007"), "officer-1", null,
            RecordedAtUtc, "new support work", "worker-1"),
            "case-reopened.v1.json"];
        yield return [new CaseObservationRecorded(
            CaseId, 3, 1, Guid.Parse("20000000-0000-0000-0000-000000000008"), "officer-1", OccurredAtUtc,
            RecordedAtUtc, Guid.Parse("40000000-0000-0000-0000-000000000001"), "subject resting, breathing normal", "home-visit"),
            "case-observation-recorded.v1.json"];
        yield return [new CaseFollowUpScheduled(
            CaseId, 3, 1, Guid.Parse("20000000-0000-0000-0000-000000000009"), "officer-1", null,
            RecordedAtUtc, Guid.Parse("40000000-0000-0000-0000-000000000002"), "revisit holding", "worker-1",
            new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero)),
            "case-follow-up-scheduled.v1.json"];
        yield return [new ActionOwnerChanged(
            CaseId, 4, 1, Guid.Parse("20000000-0000-0000-0000-000000000016"), "officer-1", null,
            RecordedAtUtc, ActionOwnerKind.FollowUp, Guid.Parse("40000000-0000-0000-0000-000000000002"),
            "worker-1", "worker-2", "coverage handoff"), "action-owner-changed.v1.json"];
        yield return [new CaseFollowUpAcknowledged(
            CaseId, 4, 1, Guid.Parse("20000000-0000-0000-0000-000000000015"), "worker-1", null, RecordedAtUtc,
            Guid.Parse("40000000-0000-0000-0000-000000000002")),
            "case-follow-up-acknowledged.v1.json"];
        yield return [new CaseFollowUpOutcomeRecorded(
            CaseId, 4, 1, Guid.Parse("20000000-0000-0000-0000-000000000010"), "worker-1", null,
            RecordedAtUtc, Guid.Parse("40000000-0000-0000-0000-000000000002"), FollowUpOutcome.Missed,
            "nobody present at holding", new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero), null, "attempt logged"),
            "case-follow-up-outcome-missed.v1.json"];
        yield return [new CaseFollowUpOutcomeRecorded(
            CaseId, 5, 1, Guid.Parse("20000000-0000-0000-0000-000000000011"), "officer-1", null,
            RecordedAtUtc, Guid.Parse("40000000-0000-0000-0000-000000000002"), FollowUpOutcome.Rescheduled,
            "retry after market day", new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), "new attempt"),
            "case-follow-up-outcome-rescheduled.v1.json"];
        yield return [new CaseEscalationRaised(
            CaseId, 3, 1, Guid.Parse("20000000-0000-0000-0000-000000000012"), "worker-1", null,
            RecordedAtUtc, Guid.Parse("40000000-0000-0000-0000-000000000003"), "holding unreachable twice",
            ["evidence-1"], "duty-officer"),
            "case-escalation-raised.v1.json"];
        yield return [new CaseEscalationAcknowledged(
            CaseId, 4, 1, Guid.Parse("20000000-0000-0000-0000-000000000013"), "officer-1", null,
            RecordedAtUtc, Guid.Parse("40000000-0000-0000-0000-000000000003"), "supervisor paged", "acknowledged"),
            "case-escalation-acknowledged.v1.json"];
        yield return [new CaseEscalationActionRecorded(
            CaseId, 5, 1, Guid.Parse("20000000-0000-0000-0000-000000000015"), "officer-1", null,
            RecordedAtUtc, Guid.Parse("40000000-0000-0000-0000-000000000003"), "Supervisor paged", "Duty officer coordinated a visit."),
            "case-escalation-action-recorded.v1.json"];
        yield return [new CaseEscalationClosed(
            CaseId, 5, 1, Guid.Parse("20000000-0000-0000-0000-000000000014"), "officer-1", null,
            RecordedAtUtc, Guid.Parse("40000000-0000-0000-0000-000000000003"), "supervisor visited holding", "resolved"),
            "case-escalation-closed.v1.json"];
    }

    public static IEnumerable<object[]> AssistanceEvents()
    {
        var assistanceId = Guid.Parse("40000000-0000-0000-0000-000000000099");
        var commandId = Guid.Parse("20000000-0000-0000-0000-000000000099");
        yield return [new AssistanceRequested(CaseId, 2, 1, commandId, "worker-1", OccurredAtUtc, RecordedAtUtc,
            assistanceId, "Transport", "worker-2", new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero)), "AssistanceRequested"];
        yield return [new AssistanceAccepted(CaseId, 3, 1, commandId, "worker-2", OccurredAtUtc, RecordedAtUtc, assistanceId, "Accepted request"), "AssistanceAccepted"];
        yield return [new AssistanceProgressRecorded(CaseId, 3, 1, commandId, "worker-2", OccurredAtUtc, RecordedAtUtc, assistanceId, "Coordinating transport"), "AssistanceProgressRecorded"];
        yield return [new AssistanceProvided(CaseId, 3, 1, commandId, "worker-2", OccurredAtUtc, RecordedAtUtc, assistanceId, "Transport arranged"), "AssistanceProvided"];
        yield return [new AssistanceReceiptConfirmed(CaseId, 4, 1, commandId, "worker-1", OccurredAtUtc, RecordedAtUtc,
            assistanceId, "Recipient confirmed delivery", "Phone confirmation", "Called the assigned recipient"), "AssistanceReceiptConfirmed"];
        yield return [new AssistanceUnfulfilled(CaseId, 4, 1, commandId, "worker-2", OccurredAtUtc, RecordedAtUtc, assistanceId, "No vehicle was available"), "AssistanceUnfulfilled"];
        yield return [new AssistanceCancelled(CaseId, 4, 1, commandId, "worker-1", OccurredAtUtc, RecordedAtUtc, assistanceId, "Request no longer needed"), "AssistanceCancelled"];
    }

    public static IEnumerable<object[]> AdditionalV1Events()
    {
        var commandId = Guid.Parse("20000000-0000-0000-0000-000000000098");
        var grantId = Guid.Parse("40000000-0000-0000-0000-000000000098");
        yield return [new CaseAssignmentAcknowledged(CaseId, 3, 1, commandId, "worker-1", OccurredAtUtc, RecordedAtUtc, 2), "CaseAssignmentAcknowledged", "assignmentVersion"];
        yield return [new CaseCollaboratorGranted(CaseId, 3, 1, commandId, "officer-1", OccurredAtUtc, RecordedAtUtc,
            grantId, "worker-2", CaseCollaborationActions.ReadCase | CaseCollaborationActions.ManageFollowUp, RecordedAtUtc.AddDays(2), "Follow-up coverage"), "CaseCollaboratorGranted", "actions"];
        yield return [new CaseCollaboratorRevoked(CaseId, 4, 1, commandId, "officer-1", OccurredAtUtc, RecordedAtUtc,
            grantId, "Coverage ended"), "CaseCollaboratorRevoked", "reason"];
    }

    [Theory]
    [MemberData(nameof(AssistanceEvents))]
    public void AssistanceEvents_UseVersionOneContractAndExpectedPayload(CaseEvent @event, string eventType)
    {
        var serialized = Serializer.SerializeToUtf8Bytes(@event);
        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;
        var payload = root.GetProperty("payload");

        Assert.Equal(1, Serializer.GetSchemaVersion(@event));
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(eventType, root.GetProperty("eventType").GetString());
        Assert.Equal("40000000-0000-0000-0000-000000000099", payload.GetProperty("assistanceId").GetString());
        if (@event is AssistanceRequested)
        {
            Assert.Equal("Transport", payload.GetProperty("need").GetString());
            Assert.Equal("worker-2", payload.GetProperty("responsibleActorId").GetString());
        }
        else if (@event is AssistanceReceiptConfirmed)
        {
            Assert.Equal("Phone confirmation", payload.GetProperty("receiptMethod").GetString());
            Assert.Equal("Called the assigned recipient", payload.GetProperty("receiptContext").GetString());
        }
        else if (@event is AssistanceActivityRecorded)
        {
            Assert.True(payload.TryGetProperty("note", out _));
            Assert.False(payload.TryGetProperty("receiptMethod", out _));
            Assert.False(payload.TryGetProperty("receiptContext", out _));
        }
    }

    [Theory]
    [MemberData(nameof(AdditionalV1Events))]
    public void AdditionalV1Events_UseVersionOneContractAndExpectedPayload(CaseEvent @event, string eventType, string payloadProperty)
    {
        using var document = JsonDocument.Parse(Serializer.SerializeToUtf8Bytes(@event));
        var root = document.RootElement;
        var payload = root.GetProperty("payload");

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(eventType, root.GetProperty("eventType").GetString());
        Assert.True(payload.TryGetProperty(payloadProperty, out _));
        if (@event is CaseCollaboratorGranted)
        {
            var schemaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../contracts/events/case-event.v1.schema.json"));
            using var schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
            var actionsPattern = schema.RootElement.GetProperty("$defs").GetProperty("collaboratorGranted")
                .GetProperty("properties").GetProperty("actions").GetProperty("pattern").GetString()!;
            Assert.Matches(actionsPattern, payload.GetProperty("actions").GetString()!);
        }
    }

    [Theory]
    [MemberData(nameof(GoldenEvents))]
    public void TypedSerializer_MatchesCommittedGoldenFixture(CaseEvent @event, string fixtureName)
    {
        var actual = Serializer.SerializeToUtf8Bytes(@event);
        var fixturePath = Path.Combine(FixtureDirectory, fixtureName);
        var expected = File.ReadAllText(fixturePath).Replace("\r\n", "\n", StringComparison.Ordinal);
        var actualText = Encoding.UTF8.GetString(actual);

        Assert.DoesNotContain('\r', actualText);
        Assert.EndsWith("\n", actualText);
        Assert.Equal(expected, actualText);
    }

    [Theory]
    [InlineData(CaseActionReviewOutcome.Pending)]
    [InlineData(CaseActionReviewOutcome.Denied)]
    public void NonApprovedReview_SerializesWithNullLinkAllowedBySchema(CaseActionReviewOutcome outcome)
    {
        var review = new CaseActionRequestReviewed(
            CaseId, 4, 1, Guid.Parse("20000000-0000-0000-0000-000000000008"), "officer-1", null,
            RecordedAtUtc, Guid.Parse("30000000-0000-0000-0000-000000000002"), outcome, null, "review not approved");

        using var serialized = JsonDocument.Parse(Serializer.SerializeToUtf8Bytes(review));
        Assert.Equal("CaseActionRequestReviewed", serialized.RootElement.GetProperty("eventType").GetString());
        Assert.Equal(outcome.ToString(), serialized.RootElement.GetProperty("payload").GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, serialized.RootElement.GetProperty("payload").GetProperty("linkedAcceptedEventId").ValueKind);
        AssertReviewLinkConditionFromSchema(serialized.RootElement.GetProperty("payload"));
    }

    [Fact]
    public void ApprovedReview_RequiresAndSerializesLinkedAcceptedEvent()
    {
        var report = new ReportFacts(CaseKind.Human, Sector.HumanHealth, "reported details", []);
        var initialEvents = CaseAggregate.Create(
            new AcceptCaseCommand(InitialCommandId, CaseId, report, "officer-1", "workspace-1", OccurredAtUtc, RecordedAtUtc),
            new ActorCapabilities("officer-1", CaseCapability.AcceptReport));
        var aggregate = CaseAggregate.Replay(initialEvents);
        var requestId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var requestEvents = aggregate.Decide(
            new RequestCaseActionCommand(Guid.Parse("20000000-0000-0000-0000-000000000002"), aggregate.AggregateVersion,
                requestId, CaseAction.Reassign, "request transfer", [], "worker-2", null, RecordedAtUtc),
            new ActorCapabilities("worker-1", CaseCapability.RequestProtectedAction));
        var requested = CaseAggregate.Replay(initialEvents.Concat(requestEvents));
        var reviewEvents = requested.Decide(
            new ReviewCaseActionRequestCommand(Guid.Parse("20000000-0000-0000-0000-000000000003"), requested.AggregateVersion,
                requestId, CaseActionReviewOutcome.Approved, null, "authorized transfer", null, RecordedAtUtc),
            new ActorCapabilities("officer-1", CaseCapability.ReviewProtectedAction | CaseCapability.AssignCase));
        var assignment = Assert.IsType<CaseAssigned>(reviewEvents[0]);
        var review = Assert.IsType<CaseActionRequestReviewed>(reviewEvents[1]);

        Assert.Equal(requestId, review.RequestId);
        Assert.Equal(assignment.EventId, review.LinkedAcceptedEventId);
        using var serialized = JsonDocument.Parse(Serializer.SerializeToUtf8Bytes(review));
        var payload = serialized.RootElement.GetProperty("payload");
        Assert.Equal(requestId, payload.GetProperty("requestId").GetGuid());
        Assert.Equal(assignment.EventId, payload.GetProperty("linkedAcceptedEventId").GetGuid());
        AssertReviewLinkConditionFromSchema(payload);
    }

    [Fact]
    public void SchemaConditional_RejectsApprovedReviewWithoutLinkAndDeniedReviewWithLink()
    {
        var invalidReviews = new[]
        {
            new CaseActionRequestReviewed(CaseId, 5, 1, Guid.NewGuid(), "officer-1", null, RecordedAtUtc,
                Guid.NewGuid(), CaseActionReviewOutcome.Approved, null, "missing accepted event"),
            new CaseActionRequestReviewed(CaseId, 4, 1, Guid.NewGuid(), "officer-1", null, RecordedAtUtc,
                Guid.NewGuid(), CaseActionReviewOutcome.Denied, Guid.NewGuid(), "denied with invalid link")
        };

        foreach (var review in invalidReviews)
        {
            Assert.Throws<InvalidDataException>(() => Serializer.SerializeToUtf8Bytes(review));
        }
    }

    [Fact]
    public void TypedHumanReport_UsesV2EnvelopeAndPreservesEvidenceMissingnessAndConfirmations()
    {
        var reportData = CreateHumanReport();
        var events = CaseAggregate.CreateManagedCase(CaseId, InitialCommandId, "workspace-1", reportData,
            OccurredAtUtc, RecordedAtUtc, new ActorCapabilities("officer-1", CaseCapability.AcceptReport));
        var serialized = Serializer.SerializeToUtf8Bytes(events[0]);
        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;
        var report = root.GetProperty("payload").GetProperty("report");

        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Human", report.GetProperty("reportKind").GetString());
        Assert.Equal("reported source narrative", report.GetProperty("evidence").GetProperty("narrative").GetProperty("text").GetString());
        Assert.Equal("Unknown", report.GetProperty("human").GetProperty("ageYears").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("human").GetProperty("ageYears").GetProperty("value").ValueKind);
        Assert.Equal("ZA", report.GetProperty("confirmations").GetProperty("occurrenceCountryCode").GetString());
        Assert.Equal(2, Serializer.GetSchemaVersion(events[0]));
        Assert.Equal(1, Serializer.GetSchemaVersion(events[1]));
    }

    [Fact]
    public void TypedAnimalReport_UsesDistinctSubjectAndCountFieldsAndRecordingConfirmation()
    {
        var provenance = Provenance(ReportSourceKind.ReporterObservation, "animal-observation");
        var context = CreateContext();
        var audioProvenance = Provenance(ReportSourceKind.OriginalAudio, "audio-1");
        var evidence = new ReportEvidence(null, [], new ReportEvidenceReference("opaque-audio-1", audioProvenance));
        var report = AnimalReport.Create(Guid.Parse("40000000-0000-0000-0000-000000000001"), context,
            AnimalSubjectScope.Herd, ReportedFact<string>.Known("cattle", provenance),
            ReportedFact<string>.Unknown(provenance), evidence,
            AnimalCount.Known(20, provenance, "herd-1"), AnimalCount.Known(4, provenance, "herd-1"),
            AnimalCount.Unknown(provenance));
        var confirmations = Confirmations(true);
        var declinedAudio = new CaseReportEventData.AnimalV2(report, confirmations with
        {
            RecordingConfirmed = false,
            RecordingPolicyReference = "recording-notice",
            RecordingPolicyVersion = "2026-01"
        });
        Assert.Throws<ArgumentException>(() => CaseAggregate.CreateManagedCase(CaseId, InitialCommandId,
            "workspace-1", declinedAudio, OccurredAtUtc, RecordedAtUtc,
            new ActorCapabilities("officer-1", CaseCapability.AcceptReport)));

        var reportData = declinedAudio with
        {
            Confirmations = declinedAudio.Confirmations with { RecordingConfirmed = true }
        };
        var events = CaseAggregate.CreateManagedCase(CaseId, InitialCommandId, "workspace-1", reportData,
            OccurredAtUtc, RecordedAtUtc, new ActorCapabilities("officer-1", CaseCapability.AcceptReport));
        using var document = JsonDocument.Parse(Serializer.SerializeToUtf8Bytes(events[0]));
        var payload = document.RootElement.GetProperty("payload").GetProperty("report");

        Assert.Equal("Animal", payload.GetProperty("reportKind").GetString());
        Assert.Equal("Herd", payload.GetProperty("animal").GetProperty("subjectScope").GetString());
        Assert.Equal(20, payload.GetProperty("animal").GetProperty("animalsAtRisk").GetProperty("value").GetInt32());
        Assert.Equal("Unknown", payload.GetProperty("animal").GetProperty("animalsDead").GetProperty("state").GetString());
        Assert.True(payload.GetProperty("confirmations").GetProperty("recordingConfirmed").GetBoolean());
    }

    [Fact]
    public void TypedReportCorrection_UsesV2EnvelopeAndKeepsRevisionMetadata()
    {
        var correction = new CaseReportCorrected(CaseId, 3, 2, Guid.Parse("20000000-0000-0000-0000-000000000009"),
            "officer-1", OccurredAtUtc, RecordedAtUtc, CreateHumanReport(), "reporter clarification", 1);

        using var document = JsonDocument.Parse(Serializer.SerializeToUtf8Bytes(correction));
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("CaseReportCorrected", document.RootElement.GetProperty("eventType").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("payload").GetProperty("previousReportRevision").GetInt64());
        Assert.Equal(2, Serializer.GetSchemaVersion(correction));
    }

    [Fact]
    public void ManagedCreation_RequiresExplicitConfirmationMetadata()
    {
        var valid = CreateHumanReport();
        var invalid = valid with
        {
            Confirmations = valid.Confirmations with { ProcessingPolicyVersion = " " }
        };

        Assert.Throws<ArgumentException>(() => CaseAggregate.CreateManagedCase(CaseId, InitialCommandId,
            "workspace-1", invalid, OccurredAtUtc, RecordedAtUtc,
            new ActorCapabilities("officer-1", CaseCapability.AcceptReport)));
    }

    [Fact]
    public void V2Schema_DeclaresVersionAndStrictTypedReportDiscriminator()
    {
        var schemaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../contracts/events/case-event.v2.schema.json"));
        using var schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        var root = schema.RootElement;
        Assert.Equal(2, root.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32());
        Assert.Equal(new[] { "CaseReported", "CaseReportCorrected" }, root.GetProperty("properties").GetProperty("eventType")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray());
        var report = root.GetProperty("$defs").GetProperty("report");
        Assert.Contains("reportKind", report.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("confirmations", report.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.True(report.GetProperty("additionalProperties").GetBoolean() == false);
        var offsetPattern = schema.RootElement.GetProperty("$defs").GetProperty("context")
            .GetProperty("properties").GetProperty("capturedOffset").GetProperty("pattern").GetString()!;
        Assert.Matches(offsetPattern, "+13:59");
        Assert.Matches(offsetPattern, "-14:00");
        Assert.DoesNotMatch(offsetPattern, "+14:30");
        Assert.DoesNotMatch(offsetPattern, "-14:01");
        var reportedTime = root.GetProperty("$defs").GetProperty("reportedTime");
        Assert.Equal(new[] { "value", "localDate", "rangeStart", "rangeEnd", "originalOffset", "zoneId", "precision" },
            reportedTime.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal(4, reportedTime.GetProperty("allOf").GetArrayLength());
    }

    [Fact]
    public void V1Schema_EventTypeVocabularyMatchesEveryCaseEventType()
    {
        var schemaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../contracts/events/case-event.v1.schema.json"));
        using var schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        var schemaEventTypes = schema.RootElement.GetProperty("properties").GetProperty("eventType")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var contractEventTypes = Enum.GetNames<CaseEventType>().OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var mappedEventTypes = schema.RootElement.GetProperty("allOf").EnumerateArray()
            .Select(condition => condition.GetProperty("if").GetProperty("properties").GetProperty("eventType"))
            .SelectMany(eventType => eventType.TryGetProperty("const", out var constant)
                ? [constant.GetString()]
                : eventType.GetProperty("enum").EnumerateArray().Select(value => value.GetString()))
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var payloadDefinitions = schema.RootElement.GetProperty("$defs").EnumerateObject().Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        var payloadReferences = schema.RootElement.GetProperty("properties").GetProperty("payload").GetProperty("oneOf")
            .EnumerateArray().Select(reference => reference.GetProperty("$ref").GetString()!.Replace("#/$defs/", "", StringComparison.Ordinal));

        Assert.Equal(contractEventTypes, schemaEventTypes);
        Assert.Equal(contractEventTypes, mappedEventTypes);
        Assert.All(payloadReferences, reference => Assert.Contains(reference, payloadDefinitions));
    }

    [Fact]
    public void ReportContext_RejectsCapturedOffsetOutsideDateTimeOffsetRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateHumanReport("+14:30"));
    }

    private static CaseReportEventData.HumanV2 CreateHumanReport(string capturedOffset = "+00:00")
    {
        var provenance = Provenance(ReportSourceKind.ReporterNarrative, "reporter-narrative-1");
        var report = HumanReport.Create(Guid.Parse("40000000-0000-0000-0000-000000000002"), CreateContext(capturedOffset),
            ReportedFact<string>.Unknown(provenance), ReportedFact<int>.Unknown(provenance),
            ReportedFact<string>.Known("reported concern", provenance),
            new ReportEvidence(new ReportedText("reported source narrative", provenance), [], null));
        return new CaseReportEventData.HumanV2(report, Confirmations(false));
    }

    private static ReportContext CreateContext(string capturedOffset = "+00:00")
    {
        var provenance = Provenance(ReportSourceKind.ReporterObservation, "occurrence-time-1");
        var occurrence = new ReportedTime(OccurredAtUtc, null, null, null, "+00:00", "Etc/UTC", ReportedTimePrecision.Exact);
        var location = new LocationContext("ZA", new OptionalPlace(null, null, PresenceState.Unknown),
            new OptionalPlace(null, null, PresenceState.Unknown), new OptionalPlace(null, null, PresenceState.Unknown),
            null, null, null, LocationSource.Reporter, LocationPrecision.Unknown, "catalogue-2026-01");
        return new ReportContext(ReportedFact<ReportedTime>.Known(occurrence, provenance), RecordedAtUtc,
            capturedOffset, capturedOffset == "+00:00" ? "Etc/UTC" : null, ReportedTimePrecision.Exact, location);
    }

    private static ReportProvenance Provenance(ReportSourceKind source, string reference) => new(source, reference);

    private static ReportConfirmations Confirmations(bool recordingPresent) => new(
        true, "ZA", "catalogue-2026-01", true, true, "processing-notice", "2026-01",
        recordingPresent ? true : null, recordingPresent ? "recording-notice" : null,
        recordingPresent ? "2026-01" : null);

    private static string FixtureDirectory => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../contracts/fixtures/case-events"));

    private static void AssertReviewLinkConditionFromSchema(JsonElement payload)
    {
        var schemaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../contracts/events/case-event.v1.schema.json"));
        using var schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        var conditions = schema.RootElement.GetProperty("$defs").GetProperty("actionReview").GetProperty("allOf");
        var outcome = payload.GetProperty("outcome").GetString();
        var link = payload.GetProperty("linkedAcceptedEventId");

        foreach (var condition in conditions.EnumerateArray())
        {
            var outcomeSchema = condition.GetProperty("if").GetProperty("properties").GetProperty("outcome");
            var matches = outcomeSchema.TryGetProperty("const", out var expected)
                ? expected.GetString() == outcome
                : outcomeSchema.GetProperty("enum").EnumerateArray().Any(value => value.GetString() == outcome);
            if (!matches) continue;

            var expectedLinkType = condition.GetProperty("then").GetProperty("properties")
                .GetProperty("linkedAcceptedEventId").GetProperty("type").GetString();
            Assert.Equal(expectedLinkType == "null" ? JsonValueKind.Null : JsonValueKind.String, link.ValueKind);
            if (expectedLinkType == "string") Assert.True(Guid.TryParse(link.GetString(), out _));
            return;
        }

        throw new Xunit.Sdk.XunitException($"No linked-event rule in the schema matches outcome '{outcome}'.");
    }
}
