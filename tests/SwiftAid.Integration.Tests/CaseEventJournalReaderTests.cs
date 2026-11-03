using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Geography;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OpenIddict.EntityFrameworkCore;

namespace SwiftAid.Integration.Tests;

public sealed class CaseEventJournalReaderTests
{
    [Fact]
    public void ActionOwnerChanged_RoundTripsAndReplaysExplicitTransfer()
    {
        var initial = CaseAggregate.CreateManagedCase(Guid.NewGuid(), Guid.NewGuid(), "workspace-1", CreateHumanReport(),
            null, RecordedAtUtc, new ActorCapabilities("worker-1", CaseCapability.AcceptReport));
        var aggregate = CaseAggregate.Replay(initial);
        var followUpId = Guid.NewGuid();
        aggregate.Apply(RoundTrip(Assert.Single(aggregate.Decide(new ScheduleFollowUpCommand(Guid.NewGuid(), aggregate.AggregateVersion,
            followUpId, "Synthetic callback", "worker-2", RecordedAtUtc.AddDays(1), "scheduled", null, RecordedAtUtc),
            new("officer-1", CaseCapability.ManageFollowUp)))));
        var transfer = Assert.Single(aggregate.Decide(new ChangeActionOwnerCommand(Guid.NewGuid(), aggregate.AggregateVersion,
            ActionOwnerKind.FollowUp, followUpId, "worker-3", "coverage change", null, RecordedAtUtc),
            new("officer-1", CaseCapability.ManageFollowUp)));

        aggregate.Apply(RoundTrip(transfer));

        var replayed = CaseAggregate.Replay(aggregate.Events);
        Assert.Equal("worker-3", Assert.Single(replayed.FollowUps).AssigneeActorId);
        Assert.IsType<ActionOwnerChanged>(replayed.Events[^1]);
    }

    [Fact]
    public void CaseEventDatabaseConstraintMatchesV1SchemaVocabulary()
    {
        var options = new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql("Host=localhost;Database=contract;Username=contract;Password=contract")
            .UseOpenIddict()
            .Options;
        using var db = new SwiftAidDbContext(options);
        var eventEntity = db.GetService<IDesignTimeModel>().Model.GetEntityTypes().Single(entity => entity.GetTableName() == "case_events");
        var constraintSql = eventEntity.GetCheckConstraints().Single(constraint => constraint.Name == "ck_case_events_type").Sql;
        var databaseEventTypes = System.Text.RegularExpressions.Regex.Matches(constraintSql, "'(?<event>[^']+)'")
            .Select(match => match.Groups["event"].Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var schemaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../contracts/events/case-event.v1.schema.json"));
        using var schema = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        var schemaEventTypes = schema.RootElement.GetProperty("properties").GetProperty("eventType")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()).OrderBy(value => value, StringComparer.Ordinal).ToArray();

        Assert.Equal(schemaEventTypes, databaseEventTypes);
    }

    [Theory]
    [InlineData(AssistanceStatus.ReceiptConfirmed)]
    [InlineData(AssistanceStatus.Unfulfilled)]
    [InlineData(AssistanceStatus.Cancelled)]
    public void Assistance_round_trip_replays_actors_and_preserves_receipt_provenance(AssistanceStatus terminal)
    {
        var initial = CaseAggregate.CreateManagedCase(Guid.NewGuid(), Guid.NewGuid(), "workspace-1", CreateHumanReport(),
            null, RecordedAtUtc, new ActorCapabilities("worker-1", CaseCapability.AcceptReport));
        var aggregate = CaseAggregate.Replay(initial);
        var id = Guid.NewGuid();
        aggregate.Apply(RoundTrip(Assert.Single(aggregate.Decide(new RequestAssistanceCommand(Guid.NewGuid(), 2, id,
            "Synthetic transport", "responsible-worker", RecordedAtUtc.AddDays(1), null, RecordedAtUtc), new("requester", CaseCapability.ManageAssistance)))));
        var statuses = terminal == AssistanceStatus.ReceiptConfirmed
            ? new[] { AssistanceStatus.Accepted, AssistanceStatus.InProgress, AssistanceStatus.Provided, terminal }
            : [AssistanceStatus.Accepted, AssistanceStatus.InProgress, terminal];
        foreach (var status in statuses)
        {
            var command = new UpdateAssistanceCommand(Guid.NewGuid(), aggregate.AggregateVersion, id, status,
                "Synthetic reason/activity", status == AssistanceStatus.ReceiptConfirmed ? "Recorded recipient statement" : null,
                status == AssistanceStatus.ReceiptConfirmed ? "Synthetic phone report, not independent verification" : null, null, RecordedAtUtc);
            aggregate.Apply(RoundTrip(Assert.Single(aggregate.Decide(command, new(status.ToString(), CaseCapability.ManageAssistance)))));
        }
        var state = Assert.Single(CaseAggregate.Replay(aggregate.Events).Assistance);
        Assert.Equal(terminal, state.Status);
        Assert.Equal("requester", state.RequestedByActorId);
        Assert.Equal("responsible-worker", state.ResponsibleActorId);
        Assert.Equal("Accepted", state.AcceptedByActorId);
        if (terminal == AssistanceStatus.ReceiptConfirmed)
        {
            Assert.Equal("Provided", state.ProvidedByActorId);
            Assert.Equal("ReceiptConfirmed", state.ReceiptRecordedByActorId);
            Assert.Contains("not independent verification", state.ReceiptContext!);
        }
        else Assert.Null(state.ReceiptRecordedByActorId);
    }

    [Theory]
    [InlineData("receiptMethod", "null")]
    [InlineData("receiptContext", "\" \"")]
    [InlineData("assistanceId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("note", "null")]
    [InlineData("unexpected", "true")]
    public void Assistance_receipt_rejects_missing_or_extra_provenance(string field, string json)
    {
        var receipt = new AssistanceReceiptConfirmed(Guid.NewGuid(), 7, 1, Guid.NewGuid(), "reporter", null,
            RecordedAtUtc, Guid.NewGuid(), "Synthetic receipt", "Recipient statement", "Reported by a worker");
        var document = System.Text.Json.Nodes.JsonNode.Parse(Serializer.SerializeToUtf8Bytes(receipt))!;
        document["payload"]![field] = System.Text.Json.Nodes.JsonNode.Parse(json);
        Assert.Throws<InvalidDataException>(() => CaseEventJournalReader.Read(ToRow(receipt),
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString())));
        Assert.Throws<InvalidDataException>(() => Serializer.SerializeToUtf8Bytes(receipt with { Status = AssistanceStatus.Provided }));
    }

    [Fact]
    public void CollaboratorGrantAndRevocationRoundTripAndReplay()
    {
        var initial = CaseAggregate.CreateManagedCase(Guid.NewGuid(), Guid.NewGuid(), "workspace-1", CreateHumanReport(),
            null, RecordedAtUtc, new ActorCapabilities("worker-1", CaseCapability.AcceptReport));
        var aggregate = CaseAggregate.Replay(initial);
        var command = new GrantCaseCollaboratorCommand(Guid.NewGuid(), 2, Guid.NewGuid(), "worker-2",
            CaseCollaborationActions.ReadCase | CaseCollaborationActions.ReadAttachment | CaseCollaborationActions.RecordObservation,
            RecordedAtUtc.AddHours(1), "Synthetic scoped grant", null, RecordedAtUtc);
        var grant = Assert.Single(aggregate.Decide(command, new("officer", CaseCapability.ManageCollaborators)));
        var restoredGrant = RoundTrip(grant);
        aggregate.Apply(restoredGrant);
        var revoke = Assert.Single(aggregate.Decide(new RevokeCaseCollaboratorCommand(Guid.NewGuid(), 3, command.GrantId,
            "Synthetic revoke", null, RecordedAtUtc.AddMinutes(1)), new("officer", CaseCapability.ManageCollaborators)));
        var restoredRevoke = RoundTrip(revoke);
        var replayed = CaseAggregate.Replay(initial.Concat([restoredGrant, restoredRevoke]));
        var collaborator = Assert.Single(replayed.Collaborators);
        Assert.Equal(command.Actions, collaborator.Actions);
        Assert.Equal(command.ExpiresAtUtc, collaborator.ExpiresAtUtc);
        Assert.Equal("worker-2", collaborator.ActorId);
        Assert.Equal(revoke.RecordedAtUtc, collaborator.RevokedAtUtc);
        Assert.Equal("worker-1", replayed.AccountableOwnerId);
    }

    private static CaseEvent RoundTrip(CaseEvent source)
    {
        var bytes = Serializer.SerializeToUtf8Bytes(source);
        var restored = CaseEventJournalReader.Read(ToRow(source), bytes);
        Assert.Equal(bytes, Serializer.SerializeToUtf8Bytes(restored));
        Assert.Equal(source.EventId, restored.EventId);
        return restored;
    }

    [Theory]
    [InlineData("actions", "0")]
    [InlineData("actions", "1")]
    [InlineData("actions", "\"RecordObservation\"")]
    [InlineData("actions", "\"ReadCase, 64\"")]
    [InlineData("actions", "\"readcase\"")]
    [InlineData("expiresAtUtc", "\"2026-10-27T08:01:00+00:00\"")]
    [InlineData("expiresAtUtc", "\"2026-10-27T12:00:00+02:00\"")]
    [InlineData("collaboratorActorId", "\" \"")]
    [InlineData("grantId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("reason", "null")]
    public void CollaboratorGrantRejectsMalformedScopeAndProvenance(string field, string json)
    {
        var grant = new CaseCollaboratorGranted(Guid.NewGuid(), 3, 1, Guid.NewGuid(), "officer", null, RecordedAtUtc,
            Guid.NewGuid(), "worker-2", CaseCollaborationActions.ReadCase, RecordedAtUtc.AddHours(1), "Synthetic grant");
        var document = System.Text.Json.Nodes.JsonNode.Parse(Serializer.SerializeToUtf8Bytes(grant))!;
        document["payload"]![field] = System.Text.Json.Nodes.JsonNode.Parse(json);
        Assert.Throws<InvalidDataException>(() => CaseEventJournalReader.Read(ToRow(grant),
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString())));
    }

    [Fact]
    public void CollaboratorEventsRejectUnknownFieldsAndSqlActorMismatch()
    {
        var revoke = new CaseCollaboratorRevoked(Guid.NewGuid(), 4, 1, Guid.NewGuid(), "officer", null, RecordedAtUtc,
            Guid.NewGuid(), "Synthetic revoke");
        var bytes = Serializer.SerializeToUtf8Bytes(revoke);
        var row = ToRow(revoke);
        row.ActorId = "other-officer";
        Assert.Throws<InvalidDataException>(() => CaseEventJournalReader.Read(row, bytes));
        var document = System.Text.Json.Nodes.JsonNode.Parse(bytes)!;
        document["payload"]!["allowEverything"] = true;
        Assert.Throws<InvalidDataException>(() => CaseEventJournalReader.Read(ToRow(revoke),
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString())));
    }

    private static readonly CaseEventSerializer Serializer = new();
    private static readonly DateTimeOffset RecordedAtUtc = new(2026, 10, 27, 8, 1, 0, TimeSpan.Zero);

    [Fact]
    public void AssignmentAcknowledgementRoundTripsAndReplays()
    {
        var events = CaseAggregate.CreateManagedCase(Guid.NewGuid(), Guid.NewGuid(), "workspace-1", CreateHumanReport(),
            null, RecordedAtUtc, new ActorCapabilities("worker-1", CaseCapability.AcceptReport));
        var aggregate = CaseAggregate.Replay(events);
        var acknowledged = Assert.Single(aggregate.Decide(new AcknowledgeAssignmentCommand(Guid.NewGuid(), 2, 2, null, RecordedAtUtc),
            new("worker-1", CaseCapability.AcknowledgeAssignment)));
        var bytes = Serializer.SerializeToUtf8Bytes(acknowledged);
        var restored = Assert.IsType<CaseAssignmentAcknowledged>(CaseEventJournalReader.Read(ToRow(acknowledged), bytes));
        Assert.Equal(2, restored.AssignmentVersion);
        Assert.Equal(acknowledged.EventId, restored.EventId);
        Assert.Equal(bytes, Serializer.SerializeToUtf8Bytes(restored));
        Assert.Equal(RecordedAtUtc, CaseAggregate.Replay(events.Append(restored)).AssignmentAcknowledgedAtUtc);
    }

    [Fact]
    public void FollowUpAcknowledgementRoundTripsAndReplaysAssignedWorkerProvenance()
    {
        var initial = CaseAggregate.CreateManagedCase(Guid.NewGuid(), Guid.NewGuid(), "workspace-1", CreateHumanReport(),
            null, RecordedAtUtc, new ActorCapabilities("officer-1", CaseCapability.AcceptReport));
        var aggregate = CaseAggregate.Replay(initial);
        var followUpId = Guid.NewGuid();
        aggregate.Apply(RoundTrip(Assert.Single(aggregate.Decide(new ScheduleFollowUpCommand(Guid.NewGuid(), 2, followUpId,
            "record next reported observation", "worker-1", RecordedAtUtc.AddDays(2), "staff scheduled", null, RecordedAtUtc),
            new ActorCapabilities("officer-1", CaseCapability.ManageFollowUp)))));
        var acknowledgement = Assert.Single(aggregate.Decide(new AcknowledgeFollowUpCommand(Guid.NewGuid(), aggregate.AggregateVersion,
            followUpId, null, RecordedAtUtc.AddMinutes(1)), new ActorCapabilities("worker-1", CaseCapability.ManageFollowUp)));
        aggregate.Apply(RoundTrip(acknowledgement));

        var replayed = CaseAggregate.Replay(initial.Concat(aggregate.Events.Skip(initial.Count)));
        var followUp = Assert.Single(replayed.FollowUps);
        Assert.Equal(FollowUpStatus.Acknowledged, followUp.Status);
        Assert.Equal("worker-1", Assert.IsType<CaseFollowUpAcknowledged>(acknowledgement).ActorId);
        Assert.Equal(followUpId, followUp.FollowUpId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(4)]
    public void AssignmentAcknowledgementRejectsInvalidAssignmentVersions(long version)
    {
        var acknowledged = new CaseAssignmentAcknowledged(Guid.NewGuid(), 3, 1, Guid.NewGuid(), "worker-1", null, RecordedAtUtc, 2);
        var document = System.Text.Json.Nodes.JsonNode.Parse(Serializer.SerializeToUtf8Bytes(acknowledged))!;
        document["payload"]!["assignmentVersion"] = version;
        Assert.Throws<InvalidDataException>(() => CaseEventJournalReader.Read(ToRow(acknowledged),
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString())));
    }

    [Fact]
    public void AssignmentAcknowledgementCannotChangeJournalActor()
    {
        var acknowledged = new CaseAssignmentAcknowledged(Guid.NewGuid(), 3, 1, Guid.NewGuid(), "worker-1", null, RecordedAtUtc, 2);
        var row = ToRow(acknowledged);
        row.ActorId = "other-worker";
        Assert.Throws<InvalidDataException>(() => CaseEventJournalReader.Read(row, Serializer.SerializeToUtf8Bytes(acknowledged)));
    }

    [Fact]
    public void Read_V2TypedReport_RestoresTheTypedReportAndConfirmationWithoutLegacyProjection()
    {
        var eventData = CreateHumanReport();
        var events = CaseAggregate.CreateManagedCase(Guid.NewGuid(), Guid.NewGuid(), "workspace-1", eventData,
            null, RecordedAtUtc, new ActorCapabilities("worker-1", CaseCapability.AcceptReport));
        var reported = events[0];
        var bytes = Serializer.SerializeToUtf8Bytes(reported);

        var restored = CaseEventJournalReader.Read(ToRow(reported), bytes);
        var typed = Assert.IsType<CaseReported>(restored);
        var human = Assert.IsType<CaseReportEventData.HumanV2>(typed.ReportData);

        Assert.Null(typed.Report);
        Assert.Equal("Original reporter narrative", human.Report.Evidence.Narrative!.Text);
        Assert.Equal("processing-policy", human.Confirmations.ProcessingPolicyReference);
        Assert.Equal(2, Serializer.GetSchemaVersion(typed));
        var aggregate = CaseAggregate.Replay(events.Select(item => item.EventId == typed.EventId ? restored : item));
        Assert.Equal(2, aggregate.AggregateVersion);
        Assert.IsType<CaseReportEventData.HumanV2>(aggregate.ReportData);
    }

    [Fact]
    public void Read_V2EnvelopeWithV1RowMetadata_IsRejected()
    {
        var reportEvent = CaseAggregate.CreateManagedCase(Guid.NewGuid(), Guid.NewGuid(), "workspace-1", CreateHumanReport(),
            null, RecordedAtUtc, new ActorCapabilities("worker-1", CaseCapability.AcceptReport))[0];
        var row = ToRow(reportEvent);
        row.SchemaVersion = 1;

        Assert.Throws<InvalidDataException>(() => CaseEventJournalReader.Read(row, Serializer.SerializeToUtf8Bytes(reportEvent)));
    }

    [Fact]
    public void Read_V2SchemaForNonReportEvent_IsRejected()
    {
        var assignment = new CaseAssigned(Guid.NewGuid(), 2, 1, Guid.NewGuid(), "worker-1", null,
            RecordedAtUtc, "worker-1");
        var serializedV1 = Serializer.SerializeToUtf8Bytes(assignment);
        var invalidV2 = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Encoding.UTF8.GetString(serializedV1).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
        var row = ToRow(assignment);
        row.SchemaVersion = 2;

        Assert.Throws<InvalidDataException>(() => CaseEventJournalReader.Read(row, invalidV2));
    }

    [Fact]
    public void Read_V2AudioWithoutAffirmativeRecordingConfirmation_IsRejected()
    {
        var reportEvent = CreateAudioReportedEvent();
        var valid = System.Text.Encoding.UTF8.GetString(Serializer.SerializeToUtf8Bytes(reportEvent));
        var declined = System.Text.Encoding.UTF8.GetBytes(valid.Replace("\"recordingConfirmed\": true", "\"recordingConfirmed\": false", StringComparison.Ordinal));

        Assert.Throws<InvalidDataException>(() => CaseEventJournalReader.Read(ToRow(reportEvent), declined));
    }

    [Fact]
    public void Read_V2KnownOccurrence_RequiresExactReportedTimeKeysAndDefinedPrecisionBeforeDeserialization()
    {
        var reportEvent = CreateKnownTimeReportedEvent(animal: false);
        var valid = System.Text.Encoding.UTF8.GetString(Serializer.SerializeToUtf8Bytes(reportEvent));
        var row = ToRow(reportEvent);

        AssertInvalidTimePayload(row, valid.Replace("\"rangeStart\": null,", string.Empty, StringComparison.Ordinal));
        AssertInvalidTimePayload(row, valid.Replace("\"rangeStart\": null,", "\"unexpected\": null, \"rangeStart\": null,", StringComparison.Ordinal));
        AssertInvalidTimePayload(row, valid.Replace("\"precision\": \"Exact\"", "\"precision\": \"NotAPrecision\"", StringComparison.Ordinal));
        AssertInvalidTimePayload(row, valid.Replace("\"precision\": \"Exact\"", "\"precision\": \"Exact\", \"precision\": \"Exact\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_V2KnownAnimalObservationPeriod_RequiresExactReportedTimeKeysAndDefinedPrecision()
    {
        var reportEvent = CreateKnownTimeReportedEvent(animal: true);
        var valid = System.Text.Encoding.UTF8.GetString(Serializer.SerializeToUtf8Bytes(reportEvent));
        var row = ToRow(reportEvent);
        var marker = "\"observationPeriod\": {";
        var position = valid.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(position >= 0);
        var periodJson = valid[position..];
        Assert.Contains("\"precision\": \"Exact\"", periodJson);

        AssertInvalidTimePayload(row, ReplaceFrom(valid, marker, "\"rangeStart\": null,", string.Empty));
        AssertInvalidTimePayload(row, ReplaceFrom(valid, marker, "\"precision\": \"Exact\"", "\"precision\": \"Undefined\""));
    }

    [Fact]
    public void Read_V1Report_RemainsLegacyAndSchemaOne()
    {
        var legacy = new CaseReported(Guid.NewGuid(), 1, 1, Guid.NewGuid(), "worker-1", null, RecordedAtUtc,
            new ReportFacts(CaseKind.Human, Sector.HumanHealth, "Legacy narrative", []), "workspace-1");
        var restored = Assert.IsType<CaseReported>(CaseEventJournalReader.Read(ToRow(legacy), Serializer.SerializeToUtf8Bytes(legacy)));

        Assert.Equal(1, Serializer.GetSchemaVersion(restored));
        Assert.IsType<CaseReportEventData.LegacyV1>(restored.ReportData);
        Assert.Equal("Legacy narrative", restored.Report!.Narrative);
    }

    private static CaseEventEntity ToRow(CaseEvent @event) => new()
    {
        EventId = @event.EventId,
        CaseId = @event.CaseId,
        AggregateVersion = @event.AggregateVersion,
        ReportRevision = @event.ReportRevision,
        CommandId = @event.CommandId,
        ActorId = @event.ActorId,
        EventType = @event.GetType().Name,
        SchemaVersion = Serializer.GetSchemaVersion(@event),
        OccurredAtUtc = @event.OccurredAtUtc,
        RecordedAtUtc = @event.RecordedAtUtc,
        PayloadId = Guid.NewGuid()
    };

    private static CaseReportEventData.HumanV2 CreateHumanReport()
    {
        var provenance = new ReportProvenance(ReportSourceKind.ReporterNarrative, "reporter-source-1");
        var location = new LocationContext("ZA", new OptionalPlace(null, null, PresenceState.Unknown),
            new OptionalPlace(null, null, PresenceState.Unknown), new OptionalPlace(null, null, PresenceState.Unknown),
            null, null, null, LocationSource.Reporter, LocationPrecision.Unknown, "country-catalogue-v1");
        var context = new ReportContext(ReportedFact<ReportedTime>.Unknown(provenance), null, null, null,
            ReportedTimePrecision.Unknown, location);
        var report = HumanReport.Create(Guid.NewGuid(), context,
            ReportedFact<string>.Unknown(provenance), ReportedFact<int>.NotRecorded(provenance),
            ReportedFact<string>.Unknown(provenance),
            new ReportEvidence(new ReportedText("Original reporter narrative", provenance), [], null));
        var confirmations = new ReportConfirmations(true, "ZA", "country-catalogue-v1", true,
            true, "processing-policy", "v1", null, null, null);
        return new CaseReportEventData.HumanV2(report, confirmations);
    }

    private static CaseReported CreateAudioReportedEvent()
    {
        var provenance = new ReportProvenance(ReportSourceKind.OriginalAudio, "audio-source-1");
        var location = new LocationContext("ZA", new OptionalPlace(null, null, PresenceState.Unknown),
            new OptionalPlace(null, null, PresenceState.Unknown), new OptionalPlace(null, null, PresenceState.Unknown),
            null, null, null, LocationSource.Reporter, LocationPrecision.Unknown, "country-catalogue-v1");
        var context = new ReportContext(ReportedFact<ReportedTime>.Unknown(provenance), null, null, null,
            ReportedTimePrecision.Unknown, location);
        var evidence = new ReportEvidence(null, [], new ReportEvidenceReference("opaque-audio", provenance));
        var report = HumanReport.Create(Guid.NewGuid(), context,
            ReportedFact<string>.Unknown(provenance), ReportedFact<int>.Unknown(provenance),
            ReportedFact<string>.Unknown(provenance), evidence);
        var confirmations = new ReportConfirmations(true, "ZA", "country-catalogue-v1", true,
            true, "processing-policy", "v1", true, "recording-policy", "v1");
        return new CaseReported(Guid.NewGuid(), 1, 1, Guid.NewGuid(), "worker-1", null, RecordedAtUtc,
            new CaseReportEventData.HumanV2(report, confirmations), "workspace-1");
    }

    private static CaseReported CreateKnownTimeReportedEvent(bool animal)
    {
        var provenance = new ReportProvenance(ReportSourceKind.ReporterObservation, "time-source-1");
        var location = new LocationContext("ZA", new OptionalPlace(null, null, PresenceState.Unknown),
            new OptionalPlace(null, null, PresenceState.Unknown), new OptionalPlace(null, null, PresenceState.Unknown),
            null, null, null, LocationSource.Reporter, LocationPrecision.Unknown, "country-catalogue-v1");
        var reportedTime = new ReportedTime(DateTimeOffset.Parse("2026-10-20T08:15:00+02:00"), null, null, null,
            "+02:00", "Africa/Mbabane", ReportedTimePrecision.Exact);
        var context = new ReportContext(ReportedFact<ReportedTime>.Known(reportedTime, provenance), null, null, null,
            ReportedTimePrecision.Unknown, location);
        var evidence = new ReportEvidence(new ReportedText("Source narrative", provenance), [], null);
        CaseReportEventData reportData;
        if (animal)
        {
            var observationPeriod = ReportedFact<ReportedTime>.Known(reportedTime, provenance);
            var report = AnimalReport.Create(Guid.NewGuid(), context, AnimalSubjectScope.Herd,
                ReportedFact<string>.Unknown(provenance), ReportedFact<string>.Unknown(provenance), evidence,
                AnimalCount.Known(4, provenance, "herd-1", observationPeriod),
                AnimalCount.Unknown(provenance), AnimalCount.Unknown(provenance));
            reportData = new CaseReportEventData.AnimalV2(report,
                new ReportConfirmations(true, "ZA", "country-catalogue-v1", true,
                    true, "processing-policy", "v1", null, null, null));
        }
        else
        {
            var report = HumanReport.Create(Guid.NewGuid(), context,
                ReportedFact<string>.Unknown(provenance), ReportedFact<int>.Unknown(provenance),
                ReportedFact<string>.Unknown(provenance), evidence);
            reportData = new CaseReportEventData.HumanV2(report,
                new ReportConfirmations(true, "ZA", "country-catalogue-v1", true,
                    true, "processing-policy", "v1", null, null, null));
        }

        return new CaseReported(Guid.NewGuid(), 1, 1, Guid.NewGuid(), "worker-1", null, RecordedAtUtc, reportData, "workspace-1");
    }

    private static void AssertInvalidTimePayload(CaseEventEntity row, string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        Assert.Throws<InvalidDataException>(() => CaseEventJournalReader.Read(row, bytes));
    }

    private static string ReplaceFrom(string source, string marker, string oldValue, string newValue)
    {
        var markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        var prefix = source[..markerIndex];
        var suffix = source[markerIndex..];
        Assert.Contains(oldValue, suffix);
        return prefix + suffix.Replace(oldValue, newValue, StringComparison.Ordinal);
    }
}
