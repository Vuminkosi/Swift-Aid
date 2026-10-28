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

/// <summary>Persistence port for the authorized timeline read. Rows arrive oldest-first.</summary>
public interface ICaseTimelineStore
{
    Task<CaseTimelineHeader?> GetHeaderAsync(Guid caseId, CancellationToken ct);

    Task<IReadOnlyList<CaseTimelineRow>> GetEventsAsync(Guid caseId, int limit, CancellationToken ct);

    /// <summary>Guest intake receipt that promoted into this case, if any.</summary>
    Task<Guid?> GetPromotingReceiptAsync(Guid caseId, CancellationToken ct);
}

public interface ICaseTimelineQuery
{
    Task<CaseTimelineResult> GetAsync(
        ActorContext actor,
        Guid caseId,
        int? limit,
        CancellationToken ct);
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
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var take = limit ?? DefaultLimit;
        if (take < 1 || take > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The timeline limit must be between 1 and 500.");
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

            var rows = await _store.GetEventsAsync(caseId, take, ct).ConfigureAwait(false);
            var entries = rows
                .OrderBy(row => row.AggregateVersion)
                .ThenBy(row => row.RecordedAtUtc)
                .Select(MapAuthoritative)
                .ToList();

            var receiptId = await _store.GetPromotingReceiptAsync(caseId, ct).ConfigureAwait(false);
            if (receiptId.HasValue)
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
            return new CaseTimelineResult(
                CaseTimelineAccess.Found,
                new CaseTimeline(header, entries));
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
        "CaseReportCorrected" => "Report corrected",
        "CaseActionRequested" => "Protected action requested",
        "CaseActionRequestReviewed" => "Protected action reviewed",
        "CaseResolved" => "Case resolved",
        "CaseReopened" => "Case reopened",
        "CaseObservationRecorded" => "Observation recorded",
        "CaseFollowUpScheduled" => "Follow-up scheduled",
        "CaseFollowUpOutcomeRecorded" => "Follow-up outcome recorded",
        "CaseEscalationRaised" => "Escalation raised",
        "CaseEscalationAcknowledged" => "Escalation acknowledged",
        "CaseEscalationClosed" => "Escalation closed",
        _ => $"Recorded event ({eventType}, schema v{schemaVersion})"
    };
}
