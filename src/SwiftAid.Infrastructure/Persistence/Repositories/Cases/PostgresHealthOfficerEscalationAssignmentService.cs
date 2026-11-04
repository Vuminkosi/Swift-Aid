using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>Atomically links an approved escalation to a normal case follow-up and receipt.</summary>
public sealed class PostgresHealthOfficerEscalationAssignmentService(SwiftAidDbContext db, ICaseCommandUnitOfWork unitOfWork,
    ICaseAccessPolicy policy, ICaseCommandPersistence persistence, IEligibleHealthOfficerStore officers,
    HealthOfficerDirectoryOptions options) : IHealthOfficerEscalationAssignmentService
{
    private static readonly CaseOperation[] Required = [CaseOperation.ReadCase, CaseOperation.ManageEscalation, CaseOperation.ManageFollowUp];

    public async Task<HealthOfficerEscalationAssignmentResult> AssignAsync(ActorContext actor, HealthOfficerEscalationAssignmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (!options.Enabled || !options.EscalationApprovalsEnabled || !options.EscalationAssignmentsEnabled)
            return new(HealthOfficerEscalationAssignmentStatus.Unavailable);
        if (request.CaseId == Guid.Empty || request.ApprovalId == Guid.Empty || request.CommandId == Guid.Empty)
            return new(HealthOfficerEscalationAssignmentStatus.Invalid);

        ICaseCommandLease preliminary;
        try { preliminary = await unitOfWork.BeginCaseAsync(actor, request.CaseId, [], CaseLeaseMode.Write, ct).ConfigureAwait(false); }
        catch (KeyNotFoundException) { return new(HealthOfficerEscalationAssignmentStatus.Denied); }
        catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.")
        { return new(HealthOfficerEscalationAssignmentStatus.Denied); }
        Approval? approval;
        await using (preliminary.ConfigureAwait(false))
        {
            if (!(await policy.AuthorizeAllAsync(actor, request.CaseId, Required, ct).ConfigureAwait(false)).IsAllowed)
                return new(HealthOfficerEscalationAssignmentStatus.Denied);
            var replay = await ReplayAsync(actor, request, ct).ConfigureAwait(false);
            if (replay is not null) return replay;
            if (await persistence.FindReceiptAsync(preliminary, actor.ActorId, request.CommandId, ct).ConfigureAwait(false) is not null)
                return new(HealthOfficerEscalationAssignmentStatus.Conflict);
            approval = await LoadApprovalAsync(actor, request, ct).ConfigureAwait(false);
            if (approval is null) return new(HealthOfficerEscalationAssignmentStatus.Denied);
            if (!approval.Current) return new(HealthOfficerEscalationAssignmentStatus.Conflict);
        }

        ICaseCommandLease lease;
        try { lease = await unitOfWork.BeginCaseAsync(actor, request.CaseId, [approval.Officer], CaseLeaseMode.Write, ct).ConfigureAwait(false); }
        catch (KeyNotFoundException) { return new(HealthOfficerEscalationAssignmentStatus.Denied); }
        catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.")
        { return new(HealthOfficerEscalationAssignmentStatus.Denied); }
        await using (lease.ConfigureAwait(false))
        {
            var decision = await policy.AuthorizeAllAsync(actor, request.CaseId, Required, ct).ConfigureAwait(false);
            if (!decision.IsAllowed) return new(HealthOfficerEscalationAssignmentStatus.Denied);
            if (string.IsNullOrWhiteSpace(decision.MatchedGrantGroupId)) return new(HealthOfficerEscalationAssignmentStatus.Unavailable);
            var replay = await ReplayAsync(actor, request, ct).ConfigureAwait(false);
            if (replay is not null) return replay;
            if (await persistence.FindReceiptAsync(lease, actor.ActorId, request.CommandId, ct).ConfigureAwait(false) is not null)
                return new(HealthOfficerEscalationAssignmentStatus.Conflict);
            var current = await LoadApprovalAsync(actor, request, ct).ConfigureAwait(false);
            if (current is null) return new(HealthOfficerEscalationAssignmentStatus.Denied);
            if (!current.Current || current.Officer != approval.Officer) return new(HealthOfficerEscalationAssignmentStatus.Conflict);
            if (!await officers.IsEligibleForAssignmentAsync(lease, current.Officer, current.DesignationId, current.DesignationVersion, ct).ConfigureAwait(false))
                return new(HealthOfficerEscalationAssignmentStatus.Denied);

            var header = await persistence.ReadCaseHeaderAsync(lease, ct).ConfigureAwait(false);
            if (header is null || header.AggregateVersion != current.CaseVersion) return new(HealthOfficerEscalationAssignmentStatus.Conflict);
            var aggregate = await persistence.ReplayCaseAsync(lease, ct).ConfigureAwait(false);
            if (aggregate.CaseId != request.CaseId || aggregate.AggregateVersion != header.AggregateVersion || aggregate.ReportRevision != header.ReportRevision)
                return new(HealthOfficerEscalationAssignmentStatus.Unavailable);
            var now = (await persistence.GetDatabaseUtcNowAsync(lease, ct).ConfigureAwait(false)).ToUniversalTime();
            var command = new ScheduleFollowUpCommand(request.CommandId, current.CaseVersion, current.FollowUpId,
                "Health officer escalation follow-up", current.Officer, current.FollowUpDue,
                $"Approved officer escalation {request.ApprovalId:D}", null, now);
            var snapshot = CanonicalCaseCommandHasher.CreateSnapshot(actor, request.CaseId,
                CanonicalCaseCommandHasher.CurrentReceiptProtocolVersion, CanonicalCaseCommandHasher.CurrentWireCommandSchemaVersion, command);
            IReadOnlyList<CaseEvent> events;
            try { events = aggregate.Decide(command, new ActorCapabilities(actor.ActorId, CaseCapability.ManageFollowUp)); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            { return new(HealthOfficerEscalationAssignmentStatus.Conflict); }
            if (events.Count != 1 || events[0] is not CaseFollowUpScheduled scheduled || scheduled.FollowUpId != current.FollowUpId)
                return new(HealthOfficerEscalationAssignmentStatus.Unavailable);
            var authorization = new CaseCommandAuthorizationSnapshot(actor.ActorId, actor.AccessEpoch, decision.MatchedGrantGroupId,
                Required.ToImmutableArray(), header.ResourceVersion);
            var staged = await persistence.StageAcceptedCommandAsync(lease, new CaseCommandAppendRequest(snapshot, authorization, events,
                header.AggregateVersion, header.ReportRevision, header.ResourceVersion, null), ct).ConfigureAwait(false);
            if (staged.Status == CaseCommandAppendStatus.CaseVersionConflict) return new(HealthOfficerEscalationAssignmentStatus.Conflict);
            if (staged.Status != CaseCommandAppendStatus.Staged) return new(HealthOfficerEscalationAssignmentStatus.Denied);
            if (staged.EventIds.Length != 1 || staged.EventIds[0] != scheduled.EventId || staged.AggregateVersion != scheduled.AggregateVersion)
                return new(HealthOfficerEscalationAssignmentStatus.Unavailable);
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO health_officer_escalation_assignments (assignment_id, approval_id, execution_command_id, case_id,
                    escalation_id, follow_up_id, follow_up_event_id, officer_actor_id, assigned_by_actor_id,
                    acknowledgement_due_utc, assigned_at_utc, aggregate_version)
                VALUES ({Guid.NewGuid()}, {request.ApprovalId}, {request.CommandId}, {request.CaseId}, {current.EscalationId},
                    {current.FollowUpId}, {scheduled.EventId}, {current.Officer}, {actor.ActorId}, {current.AckDue}, {now}, {staged.AggregateVersion})
                ON CONFLICT DO NOTHING
                """, ct).ConfigureAwait(false);
            if (inserted != 1) return new(HealthOfficerEscalationAssignmentStatus.Conflict);
            await lease.CommitAsync(ct).ConfigureAwait(false);
            return new(HealthOfficerEscalationAssignmentStatus.Recorded, scheduled.EventId, staged.AggregateVersion);
        }
    }

    private async Task<Approval?> LoadApprovalAsync(ActorContext actor, HealthOfficerEscalationAssignmentRequest request, CancellationToken ct) =>
        await db.Database.SqlQuery<Approval>($"""
            SELECT a.officer_actor_id AS "Officer", a.designation_id AS "DesignationId", a.designation_version AS "DesignationVersion",
                a.expected_case_version AS "CaseVersion", a.escalation_id AS "EscalationId", a.follow_up_id AS "FollowUpId",
                a.acknowledgement_due_utc AS "AckDue", a.follow_up_due_utc AS "FollowUpDue",
                (a.expires_at_utc > clock_timestamp() AND a.acknowledgement_due_utc > clock_timestamp() AND
                 c.aggregate_version = a.expected_case_version AND c.workflow <> 'Resolved' AND e.status = 'Raised') AS "Current"
            FROM health_officer_escalation_approvals a JOIN managed_cases c ON c.case_id = a.case_id
            JOIN managed_case_escalations e ON e.escalation_id = a.escalation_id AND e.case_id = a.case_id
            WHERE a.approval_id = {request.ApprovalId} AND a.case_id = {request.CaseId}
                AND a.approver_actor_id = {actor.ActorId} AND a.approver_access_epoch = {actor.AccessEpoch}
            """).SingleOrDefaultAsync(ct).ConfigureAwait(false);

    private async Task<HealthOfficerEscalationAssignmentResult?> ReplayAsync(ActorContext actor, HealthOfficerEscalationAssignmentRequest request, CancellationToken ct)
    {
        var row = await db.Database.SqlQuery<Assignment>($"""
            SELECT approval_id AS "ApprovalId", case_id AS "CaseId", assigned_by_actor_id AS "ActorId",
                follow_up_event_id AS "EventId", aggregate_version AS "Version"
            FROM health_officer_escalation_assignments WHERE execution_command_id = {request.CommandId}
            """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null) return null;
        return row.ApprovalId == request.ApprovalId && row.CaseId == request.CaseId && row.ActorId == actor.ActorId
            ? new(HealthOfficerEscalationAssignmentStatus.Replayed, row.EventId, row.Version)
            : new(HealthOfficerEscalationAssignmentStatus.Conflict);
    }

    private sealed class Approval
    {
        public string Officer { get; init; } = "";
        public Guid DesignationId { get; init; }
        public long DesignationVersion { get; init; }
        public long CaseVersion { get; init; }
        public Guid EscalationId { get; init; }
        public Guid FollowUpId { get; init; }
        public DateTimeOffset AckDue { get; init; }
        public DateTimeOffset FollowUpDue { get; init; }
        public bool Current { get; init; }
    }

    private sealed class Assignment
    {
        public Guid ApprovalId { get; init; }
        public Guid CaseId { get; init; }
        public string ActorId { get; init; } = "";
        public Guid EventId { get; init; }
        public long Version { get; init; }
    }
}
