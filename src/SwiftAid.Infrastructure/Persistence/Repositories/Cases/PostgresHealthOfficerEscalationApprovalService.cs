using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>Persists an exact, short-lived approval; it neither assigns nor contacts an officer.</summary>
public sealed class PostgresHealthOfficerEscalationApprovalService(SwiftAidDbContext db, ICaseCommandUnitOfWork unitOfWork,
    ICaseAccessPolicy policy, IEligibleHealthOfficerStore officers, HealthOfficerDirectoryOptions options) : IHealthOfficerEscalationApprovalService
{
    public async Task<HealthOfficerEscalationApprovalResult> ApproveAsync(ActorContext actor, HealthOfficerEscalationApprovalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (!options.Enabled || !options.EscalationApprovalsEnabled) return new(HealthOfficerEscalationApprovalStatus.Unavailable);
        if (request.ApprovalId == Guid.Empty || request.CommandId == Guid.Empty || request.CaseId == Guid.Empty ||
            request.EscalationId == Guid.Empty || request.ExpectedCaseVersion < 2 || request.DesignationId == Guid.Empty ||
            request.DesignationVersion < 1 || request.FollowUpId == Guid.Empty || !PostgresOfficerDesignationAuthority.Identifier(request.OfficerActorId) ||
            request.AcknowledgementDueUtc == default || request.FollowUpDueUtc < request.AcknowledgementDueUtc)
            return new(HealthOfficerEscalationApprovalStatus.Invalid);
        var ackDue = Normalize(request.AcknowledgementDueUtc);
        var followUpDue = Normalize(request.FollowUpDueUtc);

        // Authenticate the case before using a request-supplied officer as a lock hint.
        ICaseCommandLease preliminary;
        try { preliminary = await unitOfWork.BeginCaseAsync(actor, request.CaseId, [], CaseLeaseMode.Write, ct).ConfigureAwait(false); }
        catch (KeyNotFoundException) { return new(HealthOfficerEscalationApprovalStatus.Denied); }
        catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.")
        { return new(HealthOfficerEscalationApprovalStatus.Denied); }
        await using (preliminary.ConfigureAwait(false))
        {
            if (!await AllowedAsync(actor, request.CaseId, ct).ConfigureAwait(false)) return new(HealthOfficerEscalationApprovalStatus.Denied);
            var replay = await ReplayAsync(actor, request, ackDue, followUpDue, ct).ConfigureAwait(false);
            if (replay is not null) return replay;
        }

        ICaseCommandLease lease;
        try { lease = await unitOfWork.BeginCaseAsync(actor, request.CaseId, [request.OfficerActorId], CaseLeaseMode.Write, ct).ConfigureAwait(false); }
        catch (KeyNotFoundException) { return new(HealthOfficerEscalationApprovalStatus.Denied); }
        catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.")
        { return new(HealthOfficerEscalationApprovalStatus.Denied); }
        await using (lease.ConfigureAwait(false))
        {
            if (!await AllowedAsync(actor, request.CaseId, ct).ConfigureAwait(false)) return new(HealthOfficerEscalationApprovalStatus.Denied);
            var replay = await ReplayAsync(actor, request, ackDue, followUpDue, ct).ConfigureAwait(false);
            if (replay is not null) return replay;
            var valid = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM managed_cases c
                    JOIN managed_case_escalations e ON e.case_id = c.case_id AND e.escalation_id = {request.EscalationId}
                    JOIN case_events ev ON ev.case_id = c.case_id AND ev.aggregate_version = e.raised_version AND ev.event_type = 'CaseEscalationRaised'
                    WHERE c.case_id = {request.CaseId} AND c.aggregate_version = {request.ExpectedCaseVersion}
                        AND c.workflow <> 'Resolved' AND e.status = 'Raised' AND {ackDue} > clock_timestamp()) AS "Value"
                """).SingleAsync(ct).ConfigureAwait(false);
            if (!valid) return new(HealthOfficerEscalationApprovalStatus.Conflict);
            if (!await officers.IsEligibleForAssignmentAsync(lease, request.OfficerActorId, request.DesignationId, request.DesignationVersion, ct).ConfigureAwait(false))
                return new(HealthOfficerEscalationApprovalStatus.Denied);
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO health_officer_escalation_approvals (approval_id, command_id, case_id, escalation_id, approver_actor_id, approver_access_epoch,
                    officer_actor_id, designation_id, designation_version, expected_case_version, follow_up_id, acknowledgement_due_utc,
                    follow_up_due_utc, approved_at_utc, expires_at_utc)
                VALUES ({request.ApprovalId}, {request.CommandId}, {request.CaseId}, {request.EscalationId}, {actor.ActorId}, {actor.AccessEpoch},
                    {request.OfficerActorId}, {request.DesignationId}, {request.DesignationVersion}, {request.ExpectedCaseVersion},
                    {request.FollowUpId}, {ackDue}, {followUpDue}, clock_timestamp(), clock_timestamp() + interval '15 minutes')
                ON CONFLICT DO NOTHING
                """, ct).ConfigureAwait(false);
            if (inserted != 1) return new(HealthOfficerEscalationApprovalStatus.Conflict);
            await lease.CommitAsync(ct).ConfigureAwait(false);
            return new(HealthOfficerEscalationApprovalStatus.Recorded, request.ApprovalId);
        }
    }

    private async Task<bool> AllowedAsync(ActorContext actor, Guid caseId, CancellationToken ct) =>
        (await policy.AuthorizeAllAsync(actor, caseId, [CaseOperation.ReadCase, CaseOperation.ManageEscalation, CaseOperation.ManageFollowUp], ct).ConfigureAwait(false)).IsAllowed;

    private async Task<HealthOfficerEscalationApprovalResult?> ReplayAsync(ActorContext actor, HealthOfficerEscalationApprovalRequest request,
        DateTimeOffset ackDue, DateTimeOffset followUpDue, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ApprovalRow>($"""
            SELECT approval_id AS "ApprovalId", case_id AS "CaseId", escalation_id AS "EscalationId", approver_actor_id AS "Approver", approver_access_epoch AS "AccessEpoch",
                officer_actor_id AS "Officer", designation_id AS "DesignationId", designation_version AS "DesignationVersion",
                expected_case_version AS "CaseVersion", follow_up_id AS "FollowUpId", acknowledgement_due_utc AS "AckDue",
                follow_up_due_utc AS "FollowUpDue" FROM health_officer_escalation_approvals WHERE command_id = {request.CommandId}
            """).ToArrayAsync(ct).ConfigureAwait(false);
        if (rows.SingleOrDefault() is not { } previous) return null;
        var same = previous.ApprovalId == request.ApprovalId && previous.CaseId == request.CaseId && previous.EscalationId == request.EscalationId &&
            previous.Approver == actor.ActorId && previous.AccessEpoch == actor.AccessEpoch && previous.Officer == request.OfficerActorId && previous.DesignationId == request.DesignationId &&
            previous.DesignationVersion == request.DesignationVersion && previous.CaseVersion == request.ExpectedCaseVersion &&
            previous.FollowUpId == request.FollowUpId && previous.AckDue == ackDue && previous.FollowUpDue == followUpDue;
        return same ? new(HealthOfficerEscalationApprovalStatus.Replayed, previous.ApprovalId) : new(HealthOfficerEscalationApprovalStatus.Conflict);
    }

    private static DateTimeOffset Normalize(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    private sealed class ApprovalRow
    {
        public Guid ApprovalId { get; init; }
        public Guid CaseId { get; init; }
        public Guid EscalationId { get; init; }
        public string Approver { get; init; } = "";
        public long AccessEpoch { get; init; }
        public string Officer { get; init; } = "";
        public Guid DesignationId { get; init; }
        public long DesignationVersion { get; init; }
        public long CaseVersion { get; init; }
        public Guid FollowUpId { get; init; }
        public DateTimeOffset AckDue { get; init; }
        public DateTimeOffset FollowUpDue { get; init; }
    }
}
