using System.Collections.Immutable;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Cases;

/// <summary>One journal row projected without decrypting sealed payloads.</summary>
public sealed record CaseTimelineRow(
    Guid EventId,
    string EventType,
    long AggregateVersion,
    long ReportRevision,
    Guid CommandId,
    string ActorId,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    int SchemaVersion);

public sealed record CaseAgentActivityRow(Guid SourceEventId, Guid ActivityId, string RoleId,
    string Version, DateTimeOffset RecordedAtUtc, Guid? FollowUpId = null,
    bool? FollowUpIsCurrent = null, DateTimeOffset? FollowUpDueAtUtc = null,
    DateTimeOffset? FollowUpReviewedAtUtc = null, string? RecommendationKind = null);

public sealed record CaseOfficerEscalationRow(Guid EventId, Guid EscalationId, Guid FollowUpId, string OfficerActorId,
    string? OfficerDisplayLabel, DateTimeOffset AcknowledgementDueUtc, DateTimeOffset FollowUpDueUtc, bool IsCurrent, bool? FollowUpAcknowledged);

/// <summary>Persistence port for the authorized timeline read. Cursor pages arrive newest-first.</summary>
public interface ICaseTimelineStore
{
    Task<CaseTimelineHeader?> GetHeaderAsync(Guid caseId, CancellationToken ct);

    Task<IReadOnlyList<CaseTimelineRow>> GetEventsAsync(
        Guid caseId,
        int limit,
        long? beforeAggregateVersion,
        CancellationToken ct);

    /// <summary>Guest intake receipt that promoted into this case, if any.</summary>
    Task<Guid?> GetPromotingReceiptAsync(Guid caseId, CancellationToken ct);

    Task<IReadOnlyList<CaseCollaboratorGrantView>> GetCollaboratorsAsync(Guid caseId, CancellationToken ct);

    Task<CaseTimelineWorkSummary?> GetWorkSummaryAsync(Guid caseId, CancellationToken ct) =>
        Task.FromResult<CaseTimelineWorkSummary?>(null);

    Task<IReadOnlyList<CaseAgentActivityRow>> GetAgentActivitiesAsync(Guid caseId, IReadOnlyList<Guid> eventIds,
        bool includeFollowUp, bool includeOfficerEscalation, CancellationToken ct) => Task.FromResult<IReadOnlyList<CaseAgentActivityRow>>([]);

    Task<IReadOnlyList<CaseOfficerEscalationRow>> GetOfficerEscalationsAsync(ICaseCommandLease lease, Guid caseId, IReadOnlyList<Guid> eventIds,
        CancellationToken ct) => Task.FromResult<IReadOnlyList<CaseOfficerEscalationRow>>([]);

    Task<IReadOnlyList<CaseOfficerEscalationRow>> GetCurrentOfficerEscalationsAsync(ICaseCommandLease lease, Guid caseId,
        CancellationToken ct) => Task.FromResult<IReadOnlyList<CaseOfficerEscalationRow>>([]);
    /// <summary>Minimum attribution metadata for the original report event; no actor ID or report content.</summary>
    Task<CaseReportAttributionRow?> GetReportAttributionAsync(Guid caseId, CancellationToken ct) =>
        Task.FromResult<CaseReportAttributionRow?>(null);
}

public interface ICaseTimelineQuery
{
    Task<CaseTimelineResult> GetAsync(
        ActorContext actor,
        Guid caseId,
        int? limit,
        CancellationToken ct,
        long? beforeAggregateVersion = null);
}

/// <summary>
/// Authorized immutable-history read. Opens a read-mode case lease, enforces <c>ReadCase</c>
/// through the shared case resource policy, then projects the header and journal rows.
/// Denial, absence, and unknown cases share one outcome so callers cannot probe for case
/// existence. Sealed payloads are never decrypted for the timeline.
/// </summary>
public sealed class CaseTimelineQuery(
    ICaseTimelineStore store,
    ICaseAccessPolicy policy,
    ICaseCommandUnitOfWork unitOfWork) : ICaseTimelineQuery
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 500;

    private readonly ICaseTimelineStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ICaseAccessPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly ICaseCommandUnitOfWork _unitOfWork =
        unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));

    public async Task<CaseTimelineResult> GetAsync(
        ActorContext actor,
        Guid caseId,
        int? limit,
        CancellationToken ct,
        long? beforeAggregateVersion = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var take = limit ?? DefaultLimit;
        if (take < 1 || take > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The timeline limit must be between 1 and 500.");
        }

        if (beforeAggregateVersion is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beforeAggregateVersion), "The timeline cursor must be a positive aggregate version.");
        }

        if (caseId == Guid.Empty)
        {
            return new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
        }

        ICaseCommandLease lease;
        try
        {
            lease = await _unitOfWork.BeginCaseAsync(
                actor, caseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
        }
        catch (InvalidOperationException error) when (error.Message is
            "The case authority scope is unavailable." or "The current case authority scope is unavailable.")
        {
            return new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
        }

        await using (lease.ConfigureAwait(false))
        {
            var decision = await _policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
            }

            var header = await _store.GetHeaderAsync(caseId, ct).ConfigureAwait(false);
            if (header is null)
            {
                return new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
            }

            var canAcknowledge = false;
            if (header.AssignmentVersion is > 0 && header.AssignmentVersion <= header.AggregateVersion &&
                header.AssignmentAcknowledgedAtUtc is null && header.Workflow != "Resolved" &&
                string.Equals(header.AccountableOwnerActorId, actor.ActorId, StringComparison.Ordinal))
            {
                var acknowledgementDecision = await _policy.AuthorizeAsync(
                    actor, caseId, CaseOperation.AcknowledgeAssignment, ct).ConfigureAwait(false);
                canAcknowledge = acknowledgementDecision.IsAllowed;
            }

            header = header with { CanAcknowledgeAssignment = canAcknowledge };
            CaseCollaborationView? collaboration = null;
            var collaborationDecision = await _policy.AuthorizeAllAsync(actor, caseId,
                [CaseOperation.ReadCase, CaseOperation.ManageCollaborators], ct).ConfigureAwait(false);
            if (collaborationDecision.IsAllowed)
            {
                var grants = await _store.GetCollaboratorsAsync(caseId, ct).ConfigureAwait(false);
                collaboration = new CaseCollaborationView(header.Workflow != "Resolved", grants);
            }

            var rows = await _store.GetEventsAsync(caseId, take + 1, beforeAggregateVersion, ct).ConfigureAwait(false);
            var hasOlderEntries = rows.Count > take;
            var pageRows = rows
                .Take(take)
                .OrderBy(row => row.AggregateVersion)
                .ThenBy(row => row.RecordedAtUtc)
                .ToArray();
            var followUpDecision = await _policy.AuthorizeAllAsync(actor, caseId,
                [CaseOperation.ReadCase, CaseOperation.ManageFollowUp], ct).ConfigureAwait(false);
            var officerDecision = await _policy.AuthorizeAllAsync(actor, caseId,
                [CaseOperation.ReadCase, CaseOperation.ManageEscalation, CaseOperation.ManageFollowUp], ct).ConfigureAwait(false);
            var activityRows = await _store.GetAgentActivitiesAsync(caseId, pageRows.Select(row => row.EventId).ToArray(),
                followUpDecision.IsAllowed, officerDecision.IsAllowed, ct).ConfigureAwait(false);
            var officerRows = officerDecision.IsAllowed
                ? await _store.GetOfficerEscalationsAsync(lease, caseId, pageRows.Select(row => row.EventId).ToArray(), ct).ConfigureAwait(false)
                : [];
            var entries = pageRows.Select(row =>
            {
                var activities = activityRows.Where(activity => activity.SourceEventId == row.EventId).ToArray();
                var officer = officerRows.SingleOrDefault(item => item.EventId == row.EventId);
                return MapAuthoritative(row) with
                {
                    OfficerEscalation = officer is null ? null : new CaseOfficerEscalationAssignment(officer.EscalationId,
                        officer.FollowUpId, officer.OfficerActorId,
                        officer.OfficerDisplayLabel is { Length: > 0 and <= 120 } label && !label.Any(char.IsControl) ? label : null,
                        officer.AcknowledgementDueUtc, officer.FollowUpDueUtc, officer.IsCurrent, officer.FollowUpAcknowledged),
                    AgentActivities = activities.Take(20).Select(activity => new CaseAgentActivity(activity.ActivityId,
                        activity.RoleId, CaseAgentActivityPresentation.ActivityLabel(activity.RoleId, activity.RecommendationKind), activity.Version, activity.RecordedAtUtc)
                    {
                        FollowUpId = activity.FollowUpId,
                        FollowUpIsCurrent = activity.FollowUpIsCurrent,
                        FollowUpDueAtUtc = activity.FollowUpDueAtUtc,
                        FollowUpReviewedAtUtc = activity.FollowUpReviewedAtUtc,
                        RecommendationKind = activity.RecommendationKind
                    }).ToArray(),
                    AgentActivitiesTruncated = activities.Length > 20
                };
            }).ToList();

            // The combined summary includes escalation counts and follow-up deadlines.
            // Do not expose either through a case-read-only timeline grant.
            var workSummary = officerDecision.IsAllowed
                ? await _store.GetWorkSummaryAsync(caseId, ct).ConfigureAwait(false)
                : null;
            var currentOfficerRows = officerDecision.IsAllowed
                ? await _store.GetCurrentOfficerEscalationsAsync(lease, caseId, ct).ConfigureAwait(false)
                : [];
            var currentOfficerEscalations = currentOfficerRows.Take(10).Select(officer =>
                new CaseOfficerEscalationAssignment(officer.EscalationId, officer.FollowUpId, officer.OfficerActorId,
                    officer.OfficerDisplayLabel is { Length: > 0 and <= 120 } label && !label.Any(char.IsControl) ? label : null,
                    officer.AcknowledgementDueUtc, officer.FollowUpDueUtc, officer.IsCurrent, officer.FollowUpAcknowledged)).ToArray();
            var receiptId = await _store.GetPromotingReceiptAsync(caseId, ct).ConfigureAwait(false);
            if (receiptId.HasValue && !hasOlderEntries)
            {
                entries.Insert(0, new CaseTimelineEntry(
                    Guid.Empty,
                    "IntakePromoted",
                    $"Accepted from guest intake {receiptId.Value:D}.",
                    CaseTimelineEntryKind.Derived,
                    0,
                    header.ReportRevision,
                    Guid.Empty,
                    header.AccountableOwnerActorId,
                    null,
                    header.CreatedAtUtc,
                    1));
            }

            await lease.CommitAsync(ct).ConfigureAwait(false);
            long? olderCursor = hasOlderEntries && pageRows.Length > 0
                ? pageRows[0].AggregateVersion
                : null;
            return new CaseTimelineResult(
                CaseTimelineAccess.Found,
                new CaseTimeline(header, entries, hasOlderEntries, olderCursor, collaboration)
                {
                    WorkSummary = workSummary,
                    CurrentOfficerEscalations = currentOfficerEscalations,
                    CurrentOfficerEscalationsTruncated = currentOfficerRows.Count > 10
                });
        }
    }

    private static CaseTimelineEntry MapAuthoritative(CaseTimelineRow row) => new(
        row.EventId,
        row.EventType,
        LabelFor(row.EventType, row.SchemaVersion),
        CaseTimelineEntryKind.Authoritative,
        row.AggregateVersion,
        row.ReportRevision,
        row.CommandId,
        row.ActorId,
        row.OccurredAtUtc,
        row.RecordedAtUtc,
        row.SchemaVersion);

    private static string LabelFor(string eventType, int schemaVersion) => eventType switch
    {
        "CaseReported" => "Report accepted",
        "CaseAssigned" => "Accountable owner assigned",
        "CaseAssignmentAcknowledged" => "Assignment acknowledged",
        "CaseCollaboratorGranted" => "Collaborator access granted",
        "CaseCollaboratorRevoked" => "Collaborator access revoked",
        "CaseReportCorrected" => "Report corrected",
        "CaseActionRequested" => "Protected action requested",
        "CaseActionRequestReviewed" => "Protected action reviewed",
        "CaseResolved" => "Case resolved",
        "CaseReopened" => "Case reopened",
        "CaseDiscussionPosted" => "Discussion posted",
        "CaseObservationRecorded" => "Observation recorded",
        "CaseFollowUpScheduled" => "Follow-up scheduled",
        "CaseFollowUpAcknowledged" => "Follow-up acknowledged",
        "CaseFollowUpOutcomeRecorded" => "Follow-up outcome recorded",
        "ActionOwnerChanged" => "Action owner changed",
        "AssistanceRequested" => "Assistance requested",
        "AssistanceAccepted" => "Assistance responsibility accepted",
        "AssistanceProgressRecorded" => "Assistance progress recorded",
        "AssistanceProvided" => "Assistance provided; receipt unconfirmed",
        "AssistanceReceiptConfirmed" => "Assistance receipt confirmed",
        "AssistanceUnfulfilled" => "Assistance not fulfilled",
        "AssistanceCancelled" => "Assistance request cancelled",
        "CaseEscalationRaised" => "Escalation raised",
        "CaseEscalationAcknowledged" => "Escalation acknowledged",
        "CaseEscalationActionRecorded" => "Escalation action recorded",
        "CaseEscalationClosed" => "Escalation closed",
        _ => $"Recorded event ({eventType}, schema v{schemaVersion})"
    };
}
