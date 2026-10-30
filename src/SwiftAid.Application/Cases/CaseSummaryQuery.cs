using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases;

/// <summary>Authorized case summary fields. No sealed payloads or narratives are read.</summary>
public sealed record CaseSummaryData(
    Guid CaseId,
    CaseKind Kind,
    Sector Sector,
    ClinicalClassification Classification,
    CaseWorkflow Workflow,
    string CountryCode,
    IReadOnlyList<string> RegionIds,
    string OwnerActorId,
    string? OwnerDisplayName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>Persistence port for the case summary read.</summary>
public interface ICaseSummaryStore
{
    Task<CaseSummaryData?> GetAsync(Guid caseId, CancellationToken ct);
}

/// <summary>Access outcome for a scoped case summary read.</summary>
public enum CaseSummaryAccess
{
    Allowed,
    Denied,
    Unavailable
}

/// <summary>Case summary read outcome.</summary>
public sealed record CaseSummaryResult(
    CaseSummaryAccess Access,
    CaseSummaryData? Data);

public interface ICaseSummaryQuery
{
    Task<CaseSummaryResult> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct);
}

/// <summary>
/// Case summary over one managed case the caller may read. Enforcement runs
/// inside one read-mode lease with a <c>ReadCase</c> authorization; denied,
/// missing, and vanished cases map to denied without revealing which. Only
/// routing identity, sector, kind, classification, workflow, region links,
/// owner identity, and record times are read: no sealed payloads, narratives,
/// observations, or coordinates.
/// </summary>
public sealed class CaseSummaryQuery(
    ICaseSummaryStore store,
    ICaseAccessPolicy policy,
    ICaseCommandUnitOfWork unitOfWork) : ICaseSummaryQuery
{
    private readonly ICaseSummaryStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ICaseAccessPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly ICaseCommandUnitOfWork _unitOfWork =
        unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));

    public async Task<CaseSummaryResult> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (caseId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(caseId), "A case identifier is required.");
        }

        ICaseCommandLease lease;
        try
        {
            lease = await _unitOfWork.BeginCaseAsync(
                actor,
                caseId,
                System.Collections.Immutable.ImmutableArray<string>.Empty,
                CaseLeaseMode.Read,
                ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return new CaseSummaryResult(CaseSummaryAccess.Denied, null);
        }
        catch (Exception)
        {
            return new CaseSummaryResult(CaseSummaryAccess.Unavailable, null);
        }

        await using (lease.ConfigureAwait(false))
        {
            var decision = await _policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct)
                .ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return new CaseSummaryResult(CaseSummaryAccess.Denied, null);
            }

            CaseSummaryData? data;
            try
            {
                data = await _store.GetAsync(caseId, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return new CaseSummaryResult(CaseSummaryAccess.Unavailable, null);
            }

            if (data is null || data.CaseId != caseId)
            {
                return new CaseSummaryResult(CaseSummaryAccess.Denied, null);
            }

            await lease.CommitAsync(ct).ConfigureAwait(false);
            return new CaseSummaryResult(CaseSummaryAccess.Allowed, data);
        }
    }
}
