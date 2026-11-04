using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;

namespace SwiftAid.Application.Cases;

/// <summary>An explicitly designated officer in one qualifying grant. This is not an assignment or licence assertion.</summary>
public sealed record EligibleHealthOfficer(Guid DesignationId, long DesignationVersion, string ActorId, string? DisplayLabel);
public sealed record EligibleHealthOfficerPage(IReadOnlyList<EligibleHealthOfficer> Items, bool HasMore);

public interface IEligibleHealthOfficerStore
{
    Task<EligibleHealthOfficerPage> GetAsync(Guid caseId, int limit, CancellationToken ct);
}

public interface IEligibleHealthOfficerQuery
{
    /// <summary>Null is absent or denied. Results must be revalidated when a human approves an assignment.</summary>
    Task<EligibleHealthOfficerPage?> GetAsync(ActorContext actor, Guid caseId, int limit, CancellationToken ct);
}

public sealed class HealthOfficerDirectoryOptions
{
    public bool Enabled { get; set; }
}

public sealed class EligibleHealthOfficerQuery(IEligibleHealthOfficerStore store, ICaseAccessPolicy policy,
    ICaseCommandUnitOfWork unitOfWork, HealthOfficerDirectoryOptions options) : IEligibleHealthOfficerQuery
{
    public async Task<EligibleHealthOfficerPage?> GetAsync(ActorContext actor, Guid caseId, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (limit is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!options.Enabled || caseId == Guid.Empty) return null;
        ICaseCommandLease lease;
        try { lease = await unitOfWork.BeginCaseAsync(actor, caseId, [], CaseLeaseMode.Read, ct).ConfigureAwait(false); }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.") { return null; }
        await using (lease.ConfigureAwait(false))
        {
            var decision = await policy.AuthorizeAllAsync(actor, caseId,
                [CaseOperation.ReadCase, CaseOperation.ManageEscalation], ct).ConfigureAwait(false);
            if (!decision.IsAllowed) return null;
            var result = await store.GetAsync(caseId, limit, ct).ConfigureAwait(false);
            await lease.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
    }
}
