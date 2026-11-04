using System.Collections.Immutable;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;

namespace SwiftAid.Application.Cases;

/// <summary>Authorized minimum metadata for the original report event.</summary>
public sealed record CaseReportAttributionRow(
    Guid EventId,
    long AggregateVersion,
    string? ActorDisplayName,
    bool IsPromotedGuestIntake);

public enum CaseReportAttributionAccess
{
    Found,
    NotFoundOrNotAuthorized,
    Unavailable
}

public sealed record CaseReportAttributionResult(
    CaseReportAttributionAccess Access,
    CaseReportAttributionRow? Attribution);

public interface ICaseReportAttributionQuery
{
    Task<CaseReportAttributionResult> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct);
}

/// <summary>Reads only original report attribution behind the normal case-read lease.</summary>
public sealed class CaseReportAttributionQuery(
    ICaseTimelineStore store,
    ICaseAccessPolicy policy,
    ICaseCommandUnitOfWork unitOfWork) : ICaseReportAttributionQuery
{
    public async Task<CaseReportAttributionResult> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ct.ThrowIfCancellationRequested();
        if (caseId == Guid.Empty)
            return new(CaseReportAttributionAccess.NotFoundOrNotAuthorized, null);

        ICaseCommandLease lease;
        try
        {
            lease = await unitOfWork.BeginCaseAsync(actor, caseId, ImmutableArray<string>.Empty,
                CaseLeaseMode.Read, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return new(CaseReportAttributionAccess.NotFoundOrNotAuthorized, null);
        }

        await using (lease.ConfigureAwait(false))
        {
            var decision = await policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!decision.IsAllowed)
                return new(CaseReportAttributionAccess.NotFoundOrNotAuthorized, null);

            var attribution = await store.GetReportAttributionAsync(caseId, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (attribution is null || attribution.EventId == Guid.Empty || attribution.AggregateVersion < 1)
                return new(CaseReportAttributionAccess.Unavailable, null);

            await lease.CommitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var displayName = attribution.ActorDisplayName;
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 120 || displayName.Any(char.IsControl))
                displayName = null;
            else
                displayName = displayName.Trim();
            return new(CaseReportAttributionAccess.Found, attribution with { ActorDisplayName = displayName });
        }
    }
}
