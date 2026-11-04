using System.Globalization;
using System.Security.Cryptography;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Application.Tests;

public sealed class CanonicalCaseCommandHasherTests
{
    [Fact]
    public void Action_owner_change_hash_binds_action_kind_identity_target_and_reason()
    {
        var actor = CreateActor("officer");
        var command = new ChangeActionOwnerCommand(CommandId, 3, ActionOwnerKind.FollowUp,
            Guid.NewGuid(), "worker-2", "coverage transfer", null, Now);
        var hash = CreateSnapshot(actor, CaseId, command).CanonicalPayloadHash;
        Assert.Equal(hash, CreateSnapshot(actor, CaseId, command with { RecordedAtUtc = Now.AddMinutes(1) }).CanonicalPayloadHash);
        Assert.NotEqual(hash, CreateSnapshot(actor, CaseId, command with { ActionKind = ActionOwnerKind.Assistance }).CanonicalPayloadHash);
        Assert.NotEqual(hash, CreateSnapshot(actor, CaseId, command with { ActionId = Guid.NewGuid() }).CanonicalPayloadHash);
        Assert.NotEqual(hash, CreateSnapshot(actor, CaseId, command with { NewOwnerId = "worker-3" }).CanonicalPayloadHash);
        Assert.NotEqual(hash, CreateSnapshot(actor, CaseId, command with { Reason = "different reason" }).CanonicalPayloadHash);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(actor, CaseId, command with { ActionKind = (ActionOwnerKind)99 }));
        Assert.Throws<ArgumentException>(() => CreateSnapshot(actor, CaseId, command with { ActionId = Guid.Empty }));
    }

    [Fact]
    public void Collaborator_hash_binds_target_scope_expiry_and_grant_identity()
    {
        var actor = CreateActor("officer");
        var grant = new GrantCaseCollaboratorCommand(CommandId, 2, Guid.NewGuid(), "worker",
            CaseCollaborationActions.ReadCase, Now.AddHours(1), "Synthetic grant", null, Now);
        var hash = CreateSnapshot(actor, CaseId, grant).CanonicalPayloadHash;
        Assert.Equal(hash, CreateSnapshot(actor, CaseId, grant with { RecordedAtUtc = Now.AddMinutes(1) }).CanonicalPayloadHash);
        foreach (var changed in new[]
        {
            grant with { CollaboratorActorId = "other" },
            grant with { Actions = CaseCollaborationActions.ReadCase | CaseCollaborationActions.Synchronize },
            grant with { ExpiresAtUtc = Now.AddHours(2) },
            grant with { GrantId = Guid.NewGuid() },
            grant with { Reason = "Changed scope reason" }
        })
            Assert.NotEqual(hash, CreateSnapshot(actor, CaseId, changed).CanonicalPayloadHash);
        Assert.Throws<ArgumentException>(() => CreateSnapshot(actor, CaseId, grant with { Actions = (CaseCollaborationActions)65 }));
        Assert.Throws<ArgumentException>(() => CreateSnapshot(actor, CaseId, grant with { GrantId = Guid.Empty }));
        var revoke = new RevokeCaseCollaboratorCommand(CommandId, 2, grant.GrantId, "Synthetic revoke", null, Now);
        var revokeHash = CreateSnapshot(actor, CaseId, revoke).CanonicalPayloadHash;
        Assert.NotEqual(hash, revokeHash);
        Assert.NotEqual(revokeHash, CreateSnapshot(actor, CaseId, revoke with { GrantId = Guid.NewGuid() }).CanonicalPayloadHash);
    }

    [Fact]
    public void Assignment_acknowledgement_hash_binds_assignment_version_but_excludes_server_recorded_time()
    {
        var actor = CreateActor("worker-a");
        var command = new AcknowledgeAssignmentCommand(CommandId, 4, 2, Now, Now);
        var hash = CreateSnapshot(actor, CaseId, command).CanonicalPayloadHash;

        Assert.Equal(hash, CreateSnapshot(actor, CaseId, command with { RecordedAtUtc = Now.AddHours(1) }).CanonicalPayloadHash);
        Assert.NotEqual(hash, CreateSnapshot(actor, CaseId, command with { AssignmentVersion = 3 }).CanonicalPayloadHash);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(actor, CaseId, command with { AssignmentVersion = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(actor, CaseId, command with { AssignmentVersion = -1 }));
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid CaseId = Guid.Parse("14aa194b-15ca-42fe-857b-465fcd0e2f33");
    private static readonly Guid CommandId = Guid.Parse("b08c5c48-3a85-4bc5-9f92-222ac4abf9b4");
    private static readonly Guid RequestId = Guid.Parse("12f3749d-b55b-46be-b961-821e57203489");

    [Fact]
    public void Equivalent_timestamp_instants_hash_equally_and_snapshot_uses_utc()
    {
        var actor = CreateActor("worker-a");
        var utcCommand = CorrectReport(Now, Now);
        var offsetCommand = CorrectReport(new DateTimeOffset(2026, 10, 27, 14, 0, 0, TimeSpan.FromHours(2)), Now.AddMinutes(1));

        var utcSnapshot = CreateSnapshot(actor, CaseId, utcCommand);
        var offsetSnapshot = CreateSnapshot(actor, CaseId, offsetCommand);

        Assert.Equal(utcSnapshot.CanonicalPayloadHash, offsetSnapshot.CanonicalPayloadHash);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(offsetSnapshot.Command.OccurredAtUtc).Offset);
        Assert.Equal(Now.AddMinutes(1), offsetSnapshot.Command.RecordedAtUtc);
    }

    [Fact]
    public void Business_inputs_case_actor_command_and_expected_version_change_the_hash()
    {
        var actor = CreateActor("worker-a");
        var baseline = CreateSnapshot(actor, CaseId, CorrectReport(Now, Now));
        var actorChanged = CreateSnapshot(CreateActor("worker-b"), CaseId, CorrectReport(Now, Now));
        var caseChanged = CreateSnapshot(actor, Guid.Parse("8424b046-82f9-444e-b621-b3fb2d61983a"), CorrectReport(Now, Now));
        var commandChanged = CreateSnapshot(actor, CaseId, CorrectReport(Now, Now, commandId: Guid.NewGuid()));
        var versionChanged = CreateSnapshot(actor, CaseId, CorrectReport(Now, Now, expectedVersion: 4));
        var narrativeChanged = CreateSnapshot(actor, CaseId, CorrectReport(Now, Now, narrative: "corrected narrative 2"));
        var reasonChanged = CreateSnapshot(actor, CaseId, CorrectReport(Now, Now, reason: "different source correction"));

        Assert.NotEqual(baseline.CanonicalPayloadHash, actorChanged.CanonicalPayloadHash);
        Assert.NotEqual(baseline.CanonicalPayloadHash, caseChanged.CanonicalPayloadHash);
        Assert.NotEqual(baseline.CanonicalPayloadHash, commandChanged.CanonicalPayloadHash);
        Assert.NotEqual(baseline.CanonicalPayloadHash, versionChanged.CanonicalPayloadHash);
        Assert.NotEqual(baseline.CanonicalPayloadHash, narrativeChanged.CanonicalPayloadHash);
        Assert.NotEqual(baseline.CanonicalPayloadHash, reasonChanged.CanonicalPayloadHash);
    }

    [Fact]
    public void Every_request_payload_input_and_evidence_order_change_the_hash()
    {
        var actor = CreateActor("worker-a");
        var baselineCommand = new RequestCaseActionCommand(
            CommandId, 3, RequestId, CaseAction.Reassign, "review reassignment",
            ["event-a", "event-b"], "owner-a", Now, Now);
        var baseline = CreateSnapshot(actor, CaseId, baselineCommand);
        var changes = new[]
        {
            baselineCommand with { RequestId = Guid.NewGuid() },
            baselineCommand with { Action = CaseAction.Resolve },
            baselineCommand with { Reason = "different reason" },
            baselineCommand with { EvidenceEventIds = ["event-b", "event-a"] },
            baselineCommand with { ProposedOwnerId = "owner-b" },
            baselineCommand with { OccurredAtUtc = Now.AddSeconds(1) }
        };

        foreach (var changed in changes)
        {
            Assert.NotEqual(baseline.CanonicalPayloadHash, CreateSnapshot(actor, CaseId, changed).CanonicalPayloadHash);
        }
    }

    [Fact]
    public void Review_resolution_and_reopen_inputs_are_hash_significant()
    {
        var actor = CreateActor("worker-a");
        var review = new ReviewCaseActionRequestCommand(
            CommandId, 3, RequestId, CaseActionReviewOutcome.Approved, "owner-a", "approved request", Now, Now);
        var reviewHash = CreateSnapshot(actor, CaseId, review).CanonicalPayloadHash;
        Assert.NotEqual(reviewHash, CreateSnapshot(actor, CaseId, review with { RequestId = Guid.NewGuid() }).CanonicalPayloadHash);
        Assert.NotEqual(reviewHash, CreateSnapshot(actor, CaseId, review with { Outcome = CaseActionReviewOutcome.Denied }).CanonicalPayloadHash);
        Assert.NotEqual(reviewHash, CreateSnapshot(actor, CaseId, review with { AcceptedOwnerId = "owner-b" }).CanonicalPayloadHash);
        Assert.NotEqual(reviewHash, CreateSnapshot(actor, CaseId, review with { Reason = "different reason" }).CanonicalPayloadHash);

        var resolution = new ResolveCaseCommand(CommandId, 3, "resolve reason", Now, Now);
        var resolutionHash = CreateSnapshot(actor, CaseId, resolution).CanonicalPayloadHash;
        Assert.NotEqual(resolutionHash, CreateSnapshot(actor, CaseId, resolution with { Reason = "other reason" }).CanonicalPayloadHash);

        var reopening = new ReopenCaseCommand(CommandId, 3, "reopen reason", "owner-a", Now, Now);
        var reopeningHash = CreateSnapshot(actor, CaseId, reopening).CanonicalPayloadHash;
        Assert.NotEqual(reopeningHash, CreateSnapshot(actor, CaseId, reopening with { AccountableOwnerId = "owner-b" }).CanonicalPayloadHash);
        Assert.NotEqual(reopeningHash, CreateSnapshot(actor, CaseId, reopening with { Reason = "other reason" }).CanonicalPayloadHash);
    }

    [Fact]
    public void Access_epoch_and_server_recorded_time_are_excluded_from_the_hash()
    {
        var first = CreateSnapshot(CreateActor("worker-a", 3), CaseId, CorrectReport(Now, Now));
        var second = CreateSnapshot(CreateActor("worker-a", 91), CaseId, CorrectReport(Now, Now.AddHours(3)));

        Assert.Equal(first.CanonicalPayloadHash, second.CanonicalPayloadHash);
        Assert.NotEqual(first.Command.RecordedAtUtc, second.Command.RecordedAtUtc);
    }

    [Fact]
    public void Caller_owned_evidence_list_is_snapshotted_before_hashing_and_execution()
    {
        var actor = CreateActor("worker-a");
        var originalEvidence = new List<string> { "event-a", "event-b" };
        var command = new RequestCaseActionCommand(
            CommandId, 3, RequestId, CaseAction.Resolve, "request administrative review",
            originalEvidence, null, Now, Now);

        var snapshot = CreateSnapshot(actor, CaseId, command);
        var originalHash = snapshot.CanonicalPayloadHash;
        originalEvidence[0] = "event-mutated";
        originalEvidence.Add("event-added");

        var snapshotCommand = Assert.IsType<RequestCaseActionCommand>(snapshot.Command);
        Assert.Equal(new[] { "event-a", "event-b" }, snapshotCommand.EvidenceEventIds);
        Assert.Equal(originalHash, CreateSnapshot(actor, CaseId, snapshot.Command).CanonicalPayloadHash);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshotCommand.EvidenceEventIds)[0] = "event-replaced");

        originalHash[0] ^= 0xFF;
        Assert.NotEqual(originalHash, snapshot.CanonicalPayloadHash);
    }

    [Fact]
    public void Explicit_nulls_have_distinct_semantics_from_values()
    {
        var actor = CreateActor("worker-a");
        var withoutOwnerOrTimestamp = new ReviewCaseActionRequestCommand(
            CommandId, 3, RequestId, CaseActionReviewOutcome.Pending, null,
            "review pending", null, Now);
        var withOwner = withoutOwnerOrTimestamp with { AcceptedOwnerId = "owner-a" };
        var withTimestamp = withoutOwnerOrTimestamp with { OccurredAtUtc = Now };

        var nullOwnerHash = CreateSnapshot(actor, CaseId, withoutOwnerOrTimestamp).CanonicalPayloadHash;
        var ownerHash = CreateSnapshot(actor, CaseId, withOwner).CanonicalPayloadHash;
        var nullTimestampHash = CreateSnapshot(actor, CaseId, withoutOwnerOrTimestamp).CanonicalPayloadHash;
        var timestampHash = CreateSnapshot(actor, CaseId, withTimestamp).CanonicalPayloadHash;

        Assert.NotEqual(nullOwnerHash, ownerHash);
        Assert.Null(CreateSnapshot(actor, CaseId, withoutOwnerOrTimestamp).Command.OccurredAtUtc);
        Assert.NotEqual(nullTimestampHash, timestampHash);
    }

    [Fact]
    public void Receipt_schema_tracks_protocol_version_and_unknown_stored_schema_fails_closed()
    {
        var snapshot = CreateSnapshot(CreateActor("worker-a"), CaseId, CorrectReport(Now, Now));

        Assert.Equal("swiftaid.case-command-receipt", snapshot.ProtocolId);
        Assert.Equal(CanonicalCaseCommandHasher.CurrentReceiptProtocolVersion, snapshot.ReceiptProtocolVersion);
        Assert.Equal(CanonicalCaseCommandHasher.CurrentWireCommandSchemaVersion, snapshot.WireCommandSchemaVersion);
        Assert.Throws<NotSupportedException>(() => CanonicalCaseCommandHasher.EnsureSupportedReceiptProtocolVersion(2));
    }

    [Fact]
    public void Normative_utf8_golden_vector_preserves_unicode_nulls_and_utc_across_cultures()
    {
        var fixtureBytes = File.ReadAllBytes(GetFixturePath("v1-review-pending.json"));
        var expectedHash = File.ReadAllText(GetFixturePath("v1-review-pending.sha256")).Trim();
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(fixtureBytes)).ToLowerInvariant());

        var actor = CreateActor("worker-ü");
        var command = new ReviewCaseActionRequestCommand(
            CommandId,
            3,
            RequestId,
            CaseActionReviewOutcome.Pending,
            null,
            "révision — inconnue",
            new DateTimeOffset(2026, 10, 27, 14, 0, 0, TimeSpan.FromHours(2)),
            Now.AddHours(1));

        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var snapshot = CreateSnapshot(actor, CaseId, command);

            Assert.Equal(expectedHash, Convert.ToHexString(snapshot.CanonicalPayloadHash).ToLowerInvariant());
            Assert.Null(Assert.IsType<ReviewCaseActionRequestCommand>(snapshot.Command).AcceptedOwnerId);
            Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(snapshot.Command.OccurredAtUtc).Offset);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Fact]
    public void Unsupported_protocol_schema_creation_commands_and_invalid_ids_or_enums_fail_closed()
    {
        var actor = CreateActor("worker-a");
        var validCommand = CorrectReport(Now, Now);
        var report = new ReportFacts(CaseKind.Human, Sector.HumanHealth, "synthetic report", [new ReportedObservation("synthetic observation")]);
        var create = new AcceptCaseCommand(Guid.NewGuid(), CaseId, report, "owner-a", "workspace-a", null, Now);

        Assert.Throws<NotSupportedException>(() => CreateSnapshot(actor, CaseId, validCommand, receiptProtocolVersion: 2));
        Assert.Throws<NotSupportedException>(() => CreateSnapshot(actor, CaseId, validCommand, wireCommandSchemaVersion: 2));
        Assert.Throws<NotSupportedException>(() => CreateSnapshot(actor, CaseId, create));
        Assert.Throws<ArgumentException>(() => CreateSnapshot(actor, Guid.Empty, validCommand));
        Assert.Throws<ArgumentException>(() => CreateSnapshot(actor, CaseId, CorrectReport(Now, Now, commandId: Guid.Empty)));

        var invalidRequestId = new RequestCaseActionCommand(
            CommandId, 3, Guid.Empty, CaseAction.Resolve, "request", [], null, Now, Now);
        var invalidAction = invalidRequestId with { RequestId = RequestId, Action = (CaseAction)999 };
        var invalidEvidence = invalidRequestId with { RequestId = RequestId, EvidenceEventIds = [" "] };

        Assert.Throws<ArgumentException>(() => CreateSnapshot(actor, CaseId, invalidRequestId));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(actor, CaseId, invalidAction));
        Assert.Throws<ArgumentException>(() => CreateSnapshot(actor, CaseId, invalidEvidence));
        var reassignmentWithoutOwner = invalidRequestId with { RequestId = RequestId, Action = CaseAction.Reassign };
        Assert.Throws<ArgumentException>(() => CreateSnapshot(actor, CaseId, reassignmentWithoutOwner));
    }

    [Fact]
    public void Monitoring_commands_hash_significantly_and_reject_unknown_outcomes()
    {
        var actor = CreateActor("worker-a");
        var observation = new RecordObservationCommand(CommandId, 3, Guid.NewGuid(), "visit note", "home-visit", "record visit", Now, Now);
        var observationHash = CreateSnapshot(actor, CaseId, observation).CanonicalPayloadHash;
        Assert.NotEqual(observationHash, CreateSnapshot(actor, CaseId, observation with { Narrative = "other note" }).CanonicalPayloadHash);
        Assert.NotEqual(observationHash, CreateSnapshot(actor, CaseId, observation with { Source = "phone-call" }).CanonicalPayloadHash);
        Assert.NotEqual(observationHash, CreateSnapshot(actor, CaseId, observation with { ObservationId = Guid.NewGuid() }).CanonicalPayloadHash);

        var schedule = new ScheduleFollowUpCommand(CommandId, 3, Guid.NewGuid(), "revisit holding", "worker-1", Now.AddDays(2), "routine check", Now, Now);
        var scheduleHash = CreateSnapshot(actor, CaseId, schedule).CanonicalPayloadHash;
        Assert.NotEqual(scheduleHash, CreateSnapshot(actor, CaseId, schedule with { DueAtUtc = Now.AddDays(3) }).CanonicalPayloadHash);
        Assert.NotEqual(scheduleHash, CreateSnapshot(actor, CaseId, schedule with { AssigneeActorId = "worker-2" }).CanonicalPayloadHash);

        var outcome = new RecordFollowUpOutcomeCommand(CommandId, 3, Guid.NewGuid(), FollowUpOutcome.Missed, "nobody present", null, "attempt logged", Now, Now);
        var outcomeHash = CreateSnapshot(actor, CaseId, outcome).CanonicalPayloadHash;
        Assert.NotEqual(outcomeHash, CreateSnapshot(actor, CaseId, outcome with { Outcome = FollowUpOutcome.Unreachable }).CanonicalPayloadHash);
        var followUpAcknowledgement = new AcknowledgeFollowUpCommand(CommandId, 3, Guid.NewGuid(), Now, Now);
        var followUpAcknowledgementHash = CreateSnapshot(actor, CaseId, followUpAcknowledgement).CanonicalPayloadHash;
        Assert.NotEqual(followUpAcknowledgementHash, CreateSnapshot(actor, CaseId,
            followUpAcknowledgement with { FollowUpId = Guid.NewGuid() }).CanonicalPayloadHash);

        var escalation = new RaiseEscalationCommand(CommandId, 3, Guid.NewGuid(), "concern", ["event-a"], "duty-officer", Now, Now);
        var escalationHash = CreateSnapshot(actor, CaseId, escalation).CanonicalPayloadHash;
        Assert.NotEqual(escalationHash, CreateSnapshot(actor, CaseId, escalation with { RecipientRoute = "other-route" }).CanonicalPayloadHash);

        var acknowledge = new AcknowledgeEscalationCommand(CommandId, 3, Guid.NewGuid(), "supervisor paged", "acknowledged", Now, Now);
        var acknowledgeHash = CreateSnapshot(actor, CaseId, acknowledge).CanonicalPayloadHash;
        Assert.NotEqual(acknowledgeHash, CreateSnapshot(actor, CaseId, acknowledge with { ActionSummary = (string?)null }).CanonicalPayloadHash);

        var close = new CloseEscalationCommand(CommandId, 3, Guid.NewGuid(), "visited holding", "resolved", Now, Now);
        var closeHash = CreateSnapshot(actor, CaseId, close).CanonicalPayloadHash;
        Assert.NotEqual(closeHash, CreateSnapshot(actor, CaseId, close with { Resolution = "other resolution" }).CanonicalPayloadHash);

        var recordedAction = new RecordEscalationActionCommand(CommandId, 3, Guid.NewGuid(), "Supervisor paged", "coordinated response", Now, Now);
        var recordedActionHash = CreateSnapshot(actor, CaseId, recordedAction).CanonicalPayloadHash;
        Assert.NotEqual(recordedActionHash, CreateSnapshot(actor, CaseId, recordedAction with { ActionSummary = "Team contacted" }).CanonicalPayloadHash);

        Assert.Distinct([observationHash, scheduleHash, outcomeHash, followUpAcknowledgementHash, escalationHash, acknowledgeHash, closeHash, recordedActionHash]);

        var invalidOutcome = outcome with { Outcome = (FollowUpOutcome)999 };
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(actor, CaseId, invalidOutcome));
    }

    private static CanonicalCaseCommandSnapshot CreateSnapshot(
        ActorContext actor,
        Guid caseId,
        CaseCommand command,
        int receiptProtocolVersion = CanonicalCaseCommandHasher.CurrentReceiptProtocolVersion,
        int wireCommandSchemaVersion = CanonicalCaseCommandHasher.CurrentWireCommandSchemaVersion) =>
        CanonicalCaseCommandHasher.CreateSnapshot(actor, caseId, receiptProtocolVersion, wireCommandSchemaVersion, command);

    private static string GetFixturePath(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "contracts", "fixtures", "case-command-receipts", fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new DirectoryNotFoundException("The canonical case command receipt fixture was not found.");
    }

    private static CorrectReportCommand CorrectReport(
        DateTimeOffset? occurredAt,
        DateTimeOffset recordedAt,
        Guid? commandId = null,
        long expectedVersion = 3,
        string narrative = "corrected source narrative",
        string reason = "source correction") =>
        new(commandId ?? CommandId, expectedVersion, narrative, reason, occurredAt, recordedAt);

    private static ActorContext CreateActor(string actorId, long accessEpoch = 7) =>
        ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", actorId),
            new Claim(ActorContext.AccessEpochClaimType, accessEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
        ], authenticationType: "validated-test")));
}
