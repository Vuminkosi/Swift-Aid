using System.Collections.Immutable;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;

namespace SwiftAid.Application.Cases;

/// <summary>One non-terminal follow-up with its current due time and status.</summary>
public sealed record CaseMonitoringFollowUpItem(
    Guid FollowUpId,
    string Purpose,
    string AssigneeActorId,
    DateTimeOffset DueAtUtc,
    string Status);

/// <summary>One escalation with its current status. The case owner stays accountable throughout.</summary>
public sealed record CaseMonitoringEscalationItem(
    Guid EscalationId,
    string Status);

/// <summary>Latest observation metadata. Narrative and source are copied from the recorded event.</summary>
public sealed record CaseMonitoringObservationItem(
    Guid ObservationId,
    string Narrative,
    string Source,
    long AggregateVersion);

/// <summary>Per-case monitoring summary projected from the managed-case monitoring read models.</summary>
public sealed record CaseMonitoringSummary(
    Guid CaseId,
    IReadOnlyList<CaseMonitoringFollowUpItem> OpenFollowUps,
    IReadOnlyList<CaseMonitoringEscalationItem> Escalations,
    CaseMonitoringObservationItem? LatestObservation);

public enum CaseMonitoringSummaryAccess
{
    Found,
    NotFoundOrNotAuthorized
}

public sealed record CaseMonitoringSummaryResult(
    CaseMonitoringSummaryAccess Access,
    CaseMonitoringSummary? Summary);

/// <summary>Persistence port for the monitoring summary read. Open means not Completed or Cancelled.</summary>
public interface ICaseMonitoringSummaryStore
{
    Task<IReadOnlyList<CaseMonitoringFollowUpItem>> GetOpenFollowUpsAsync(Guid caseId, CancellationToken ct);

    Task<IReadOnlyList<CaseMonitoringEscalationItem>> GetEscalationsAsync(
        ICaseCommandLease lease, Guid caseId, CancellationToken ct);

    Task<CaseMonitoringObservationItem?> GetLatestObservationAsync(Guid caseId, CancellationToken ct);
}

public interface ICaseMonitoringSummaryQuery
{
    Task<CaseMonitoringSummaryResult> GetAsync(
        ActorContext actor,
        Guid caseId,
        CancellationToken ct);
}

/// <summary>
/// Authorized per-case monitoring summary read. Opens a read-mode case lease, enforces
/// <c>ReadCase</c> through the shared case resource policy, then projects the open
/// follow-ups, escalations, and latest observation metadata. Denial, absence, and
/// unknown cases share one outcome so callers cannot probe for case existence.
/// </summary>
public sealed class CaseMonitoringSummaryQuery(
    ICaseMonitoringSummaryStore store,
    ICaseAccessPolicy policy,
    ICaseCommandUnitOfWork unitOfWork) : ICaseMonitoringSummaryQuery
{
    private readonly ICaseMonitoringSummaryStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ICaseAccessPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly ICaseCommandUnitOfWork _unitOfWork =
        unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));

    public async Task<CaseMonitoringSummaryResult> GetAsync(
        ActorContext actor,
        Guid caseId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (caseId == Guid.Empty)
        {
            return new CaseMonitoringSummaryResult(CaseMonitoringSummaryAccess.NotFoundOrNotAuthorized, null);
        }

        ICaseCommandLease lease;
        try
        {
            lease = await _unitOfWork.BeginCaseAsync(
                actor, caseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return new CaseMonitoringSummaryResult(CaseMonitoringSummaryAccess.NotFoundOrNotAuthorized, null);
        }

        await using (lease.ConfigureAwait(false))
        {
            var decision = await _policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return new CaseMonitoringSummaryResult(CaseMonitoringSummaryAccess.NotFoundOrNotAuthorized, null);
            }

            var observation = await _store.GetLatestObservationAsync(caseId, ct).ConfigureAwait(false);
            var followUps = await _store.GetOpenFollowUpsAsync(caseId, ct).ConfigureAwait(false);
            var escalations = await _store.GetEscalationsAsync(lease, caseId, ct).ConfigureAwait(false);

            await lease.CommitAsync(ct).ConfigureAwait(false);
            return new CaseMonitoringSummaryResult(
                CaseMonitoringSummaryAccess.Found,
                new CaseMonitoringSummary(caseId, followUps, escalations, observation));
        }
    }
}
