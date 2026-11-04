using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Domain.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>
/// Reads the managed-case projection and its authoritative journal rows. Never decrypts
/// sealed payloads; the timeline carries metadata only.
/// </summary>
public sealed class PostgresCaseTimelineStore(SwiftAidDbContext dbContext) : ICaseTimelineStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<IReadOnlyList<CaseCollaboratorGrantView>> GetCollaboratorsAsync(Guid caseId, CancellationToken ct)
    {
        var rows = await (from share in _dbContext.CaseShares.AsNoTracking()
                          where share.CaseId == caseId && share.GrantId != null
                          join profile in _dbContext.UserProfiles.AsNoTracking() on share.ActorId equals profile.UserId into profiles
                          from profile in profiles.DefaultIfEmpty()
                          orderby share.GrantedAtUtc, share.ActorId
                          select new
                          {
                              share.GrantId, share.ActorId, DisplayName = profile == null ? null : profile.DisplayName,
                              share.AllowedOperations, share.ExpiresAtUtc, share.IsActive
                          }).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(row => new CaseCollaboratorGrantView(row.GrantId!.Value, row.ActorId, row.DisplayName,
            row.AllowedOperations ?? throw new InvalidDataException("A collaborator grant has no operation scope."),
            row.ExpiresAtUtc ?? throw new InvalidDataException("A collaborator grant has no expiry."), !row.IsActive)).ToArray();
    }

    public async Task<CaseTimelineWorkSummary?> GetWorkSummaryAsync(Guid caseId, CancellationToken ct)
    {
        var escalations = await _dbContext.ManagedCaseEscalations.AsNoTracking()
            .CountAsync(item => item.CaseId == caseId && item.Status != EscalationStatus.Closed, ct).ConfigureAwait(false);
        var followUps = _dbContext.ManagedCaseFollowUps.AsNoTracking().Where(item => item.CaseId == caseId &&
            (item.Status == FollowUpStatus.Scheduled || item.Status == FollowUpStatus.Acknowledged));
        var count = await followUps.CountAsync(ct).ConfigureAwait(false);
        var due = await followUps.MinAsync(item => (DateTimeOffset?)item.DueAtUtc, ct).ConfigureAwait(false);
        return new CaseTimelineWorkSummary(escalations, count, due);
    }

    public async Task<CaseTimelineHeader?> GetHeaderAsync(Guid caseId, CancellationToken ct)
    {
        var header = await _dbContext.ManagedCases.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .Select(item => new CaseTimelineHeader(
                item.CaseId,
                item.OrganizationId,
                item.WorkspaceId,
                item.CountryCode,
                item.Workflow.ToString(),
                item.AccountableOwnerActorId,
                item.AggregateVersion,
                item.ReportRevision,
                item.CreatedAtUtc))
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        // Read independently of the requested history page. A reopened case establishes
        // a new assignment even when the accountable owner has not changed.
        var assignment = await _dbContext.CaseEvents.AsNoTracking()
            .Where(item => item.CaseId == caseId && item.AggregateVersion <= header.AggregateVersion &&
                (item.EventType == "CaseAssigned" || item.EventType == "CaseReopened"))
            .OrderByDescending(item => item.AggregateVersion)
            .Select(item => new { item.AggregateVersion, item.RecordedAtUtc })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (assignment is null)
        {
            return header;
        }

        var acknowledgedAt = await _dbContext.CaseEvents.AsNoTracking()
            .Where(item => item.CaseId == caseId && item.EventType == "CaseAssignmentAcknowledged" &&
                item.AggregateVersion > assignment.AggregateVersion && item.AggregateVersion <= header.AggregateVersion &&
                item.ActorId == header.AccountableOwnerActorId)
            .OrderByDescending(item => item.AggregateVersion)
            .Select(item => (DateTimeOffset?)item.RecordedAtUtc)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return header with
        {
            AssignmentVersion = assignment.AggregateVersion,
            AssignmentRecordedAtUtc = assignment.RecordedAtUtc,
            AssignmentAcknowledgedAtUtc = acknowledgedAt
        };
    }

    public async Task<IReadOnlyList<CaseTimelineRow>> GetEventsAsync(
        Guid caseId,
        int limit,
        long? beforeAggregateVersion,
        CancellationToken ct)
    {
        var events = _dbContext.CaseEvents.AsNoTracking()
            .Where(item => item.CaseId == caseId);
        if (beforeAggregateVersion is { } cursor)
        {
            events = events.Where(item => item.AggregateVersion < cursor);
        }

        var rows = await events
            .OrderByDescending(item => item.AggregateVersion)
            .ThenByDescending(item => item.RecordedAtUtc)
            .Take(limit)
            .Select(item => new CaseTimelineRow(
                item.EventId,
                item.EventType,
                item.AggregateVersion,
                item.ReportRevision,
                item.CommandId,
                item.ActorId,
                item.OccurredAtUtc,
                item.RecordedAtUtc,
                item.SchemaVersion))
            .ToArrayAsync(ct).ConfigureAwait(false);
        return rows;
    }

    public async Task<IReadOnlyList<CaseAgentActivityRow>> GetAgentActivitiesAsync(Guid caseId,
        IReadOnlyList<Guid> eventIds, bool includeFollowUp, CancellationToken ct)
    {
        if (eventIds.Count == 0) return [];
        var ids = eventIds.ToArray();
        return await _dbContext.Database.SqlQuery<CaseAgentActivityRow>($"""
            SELECT "SourceEventId", "ActivityId", "RoleId", "Version", "RecordedAtUtc",
                "FollowUpId", "FollowUpIsCurrent", "FollowUpDueAtUtc", "FollowUpReviewedAtUtc" FROM (
                SELECT *, row_number() OVER (PARTITION BY "SourceEventId"
                    ORDER BY "RecordedAtUtc" DESC, "ActivityId" DESC) AS position FROM (
                    SELECT source_event_id AS "SourceEventId", activity_id AS "ActivityId", role_id AS "RoleId",
                        quality_version AS "Version", recorded_at_utc AS "RecordedAtUtc",
                        NULL::uuid AS "FollowUpId", NULL::boolean AS "FollowUpIsCurrent", NULL::timestamptz AS "FollowUpDueAtUtc",
                        NULL::timestamptz AS "FollowUpReviewedAtUtc"
                    FROM ellai_case_quality_activity WHERE case_id = {caseId} AND source_event_id = ANY({ids})
                    UNION ALL
                    SELECT r.source_event_id, r.activity_id, 'ops.followup', r.policy_version, r.recorded_at_utc,
                        r.follow_up_id, (f.current_schedule_version = r.schedule_version AND
                            f.current_schedule_event_id = r.source_event_id AND f.due_at_utc = r.due_at_utc AND
                            f.status IN ('Scheduled', 'Acknowledged') AND c.workflow <> 'Resolved'), r.due_at_utc, review.recorded_at_utc
                    FROM ellai_follow_up_recommendations r
                    LEFT JOIN ellai_follow_up_reviews review ON review.recommendation_id = r.activity_id
                    JOIN managed_case_follow_ups f ON f.follow_up_id = r.follow_up_id AND f.case_id = r.case_id
                    JOIN managed_cases c ON c.case_id = r.case_id
                    WHERE {includeFollowUp} AND r.case_id = {caseId} AND r.source_event_id = ANY({ids})
                ) sources
            ) activity WHERE position <= 21 ORDER BY "SourceEventId", "RecordedAtUtc" DESC, "ActivityId" DESC
            """).ToArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CaseOfficerEscalationRow>> GetOfficerEscalationsAsync(ICaseCommandLease lease, Guid caseId,
        IReadOnlyList<Guid> eventIds, CancellationToken ct)
    {
        if (lease is not PostgresCaseCommandLease current || !current.IsActive || current.Mode != CaseLeaseMode.Read ||
            current.CaseId != caseId || !ReferenceEquals(current.DbContext, _dbContext) ||
            !ReferenceEquals(current.Transaction, _dbContext.Database.CurrentTransaction))
            throw new UnauthorizedAccessException("An active case read lease is required to read officer escalation history.");
        if (eventIds.Count == 0) return [];
        var ids = eventIds.ToArray();
        return await _dbContext.Database.SqlQuery<CaseOfficerEscalationRow>($"""
            SELECT a.follow_up_event_id AS "EventId", a.escalation_id AS "EscalationId", a.follow_up_id AS "FollowUpId",
                a.officer_actor_id AS "OfficerActorId", profile.display_name AS "OfficerDisplayLabel",
                a.acknowledgement_due_utc AS "AcknowledgementDueUtc",
                CASE WHEN f.assignee_actor_id = a.officer_actor_id AND f.status IN ('Scheduled', 'Acknowledged')
                    AND e.status <> 'Closed' AND c.workflow <> 'Resolved' THEN f.due_at_utc ELSE approval.follow_up_due_utc END AS "FollowUpDueUtc",
                (f.assignee_actor_id = a.officer_actor_id AND f.status IN ('Scheduled', 'Acknowledged')
                    AND e.status <> 'Closed' AND c.workflow <> 'Resolved') AS "IsCurrent",
                CASE WHEN f.assignee_actor_id = a.officer_actor_id AND f.status IN ('Scheduled', 'Acknowledged')
                    AND e.status <> 'Closed' AND c.workflow <> 'Resolved' THEN f.status = 'Acknowledged' ELSE NULL END AS "FollowUpAcknowledged"
            FROM health_officer_escalation_assignments a
            JOIN health_officer_escalation_approvals approval ON approval.approval_id = a.approval_id
            JOIN managed_case_follow_ups f ON f.follow_up_id = a.follow_up_id AND f.case_id = a.case_id
            JOIN managed_case_escalations e ON e.escalation_id = a.escalation_id AND e.case_id = a.case_id
            JOIN managed_cases c ON c.case_id = a.case_id
            LEFT JOIN swiftaid_identity.user_profiles profile ON profile.user_id = a.officer_actor_id
            WHERE a.case_id = {caseId} AND a.follow_up_event_id = ANY({ids})
            """).ToArrayAsync(ct).ConfigureAwait(false);
    }

    public Task<Guid?> GetPromotingReceiptAsync(Guid caseId, CancellationToken ct) =>
        _dbContext.IntakePromotions.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .Select(item => (Guid?)item.ReceiptId)
            .SingleOrDefaultAsync(ct);
}
