using System.Collections.Immutable;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases;

public enum CaseCorrectionReadAccess
{
    Allowed,
    NotFoundOrNotAuthorized,
    Unavailable
}

public enum CaseCorrectionReportKind
{
    LegacyV1,
    HumanV2,
    AnimalV2
}

/// <summary>Minimal authorized input for the legacy narrative correction command.</summary>
public sealed record CaseCorrectionReadData(
    Guid CaseId,
    long AggregateVersion,
    long ReportRevision,
    CaseCorrectionReportKind ReportKind,
    bool CanCorrectReport,
    string? Narrative);

public sealed record CaseCorrectionReadResult(
    CaseCorrectionReadAccess Access,
    CaseCorrectionReadData? Data);

public interface ICaseCorrectionReadQuery
{
    Task<CaseCorrectionReadResult> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct);
}

/// <summary>
/// Opens only an authorized managed-case read and returns the legacy narrative when both
/// current read and correction grants are present. Typed reports are intentionally non-editable.
/// </summary>
public sealed class CaseCorrectionReadQuery(
    ICaseAccessPolicy policy,
    ICaseCommandUnitOfWork unitOfWork,
    ICaseCommandPersistence persistence) : ICaseCorrectionReadQuery
{
    private readonly ICaseAccessPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly ICaseCommandUnitOfWork _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    private readonly ICaseCommandPersistence _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));

    public async Task<CaseCorrectionReadResult> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (caseId == Guid.Empty)
        {
            return new CaseCorrectionReadResult(CaseCorrectionReadAccess.NotFoundOrNotAuthorized, null);
        }

        ICaseCommandLease lease;
        try
        {
            lease = await _unitOfWork.BeginCaseAsync(
                actor, caseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return new CaseCorrectionReadResult(CaseCorrectionReadAccess.NotFoundOrNotAuthorized, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new CaseCorrectionReadResult(CaseCorrectionReadAccess.Unavailable, null);
        }

        await using (lease.ConfigureAwait(false))
        {
            try
            {
                var readDecision = await _policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct)
                    .ConfigureAwait(false);
                if (IsAuthorizationSourceUnavailable(readDecision))
                {
                    return new CaseCorrectionReadResult(CaseCorrectionReadAccess.Unavailable, null);
                }

                if (!readDecision.IsAllowed)
                {
                    return new CaseCorrectionReadResult(CaseCorrectionReadAccess.NotFoundOrNotAuthorized, null);
                }

                var aggregate = await _persistence.ReplayCaseAsync(lease, ct).ConfigureAwait(false);
                if (aggregate.CaseId != caseId || aggregate.AggregateVersion < 1 || aggregate.ReportRevision < 1 ||
                    aggregate.ReportData is null)
                {
                    return new CaseCorrectionReadResult(CaseCorrectionReadAccess.Unavailable, null);
                }

                var (kind, legacyNarrative) = aggregate.ReportData switch
                {
                    CaseReportEventData.LegacyV1 legacy => (CaseCorrectionReportKind.LegacyV1, legacy.Report.Narrative),
                    CaseReportEventData.HumanV2 => (CaseCorrectionReportKind.HumanV2, null),
                    CaseReportEventData.AnimalV2 => (CaseCorrectionReportKind.AnimalV2, null),
                    _ => throw new InvalidDataException("The current report type is unsupported.")
                };

                var correctionDecision = await _policy.AuthorizeAllAsync(
                    actor,
                    caseId,
                    [CaseOperation.ReadCase, CaseOperation.CorrectReport],
                    ct).ConfigureAwait(false);
                if (IsAuthorizationSourceUnavailable(correctionDecision))
                {
                    return new CaseCorrectionReadResult(CaseCorrectionReadAccess.Unavailable, null);
                }

                if (!correctionDecision.IsAllowed)
                {
                    return new CaseCorrectionReadResult(CaseCorrectionReadAccess.NotFoundOrNotAuthorized, null);
                }

                var canCorrect = kind == CaseCorrectionReportKind.LegacyV1 && correctionDecision.IsAllowed;
                var result = new CaseCorrectionReadData(
                    caseId,
                    aggregate.AggregateVersion,
                    aggregate.ReportRevision,
                    kind,
                    canCorrect,
                    canCorrect ? legacyNarrative : null);

                await lease.CommitAsync(ct).ConfigureAwait(false);
                return new CaseCorrectionReadResult(CaseCorrectionReadAccess.Allowed, result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return new CaseCorrectionReadResult(CaseCorrectionReadAccess.Unavailable, null);
            }
        }
    }

    private static bool IsAuthorizationSourceUnavailable(AccessDecision decision) =>
        decision.Code is AccessDecisionCode.GrantSourceUnavailable or AccessDecisionCode.ResourceSourceUnavailable;
}
