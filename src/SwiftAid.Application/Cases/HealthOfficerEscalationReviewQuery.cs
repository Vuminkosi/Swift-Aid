using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;

namespace SwiftAid.Application.Cases;

public sealed record HealthOfficerEscalationReviewItem(Guid EscalationId, DateTimeOffset RaisedAtUtc);
public sealed record HealthOfficerEscalationReviewData(long CaseVersion, IReadOnlyList<HealthOfficerEscalationReviewItem> Escalations, bool HasMore);
public sealed record HealthOfficerEscalationReviewContext(long CaseVersion, IReadOnlyList<HealthOfficerEscalationReviewItem> Escalations,
    bool MoreEscalations, IReadOnlyList<EligibleHealthOfficer> Officers, bool MoreOfficers);

public interface IHealthOfficerEscalationReviewStore
{
    Task<HealthOfficerEscalationReviewData?> GetAsync(ICaseCommandLease lease, Guid caseId, CancellationToken ct);
}

public interface IHealthOfficerEscalationReviewQuery
{
    /// <summary>Null means unavailable or unauthorized; it never authorizes a later assignment.</summary>
    Task<HealthOfficerEscalationReviewContext?> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct);
}

public sealed class HealthOfficerEscalationReviewQuery(ICaseCommandUnitOfWork unitOfWork, ICaseAccessPolicy policy,
    IHealthOfficerEscalationReviewStore store, IEligibleHealthOfficerStore officers, HealthOfficerDirectoryOptions options)
    : IHealthOfficerEscalationReviewQuery
{
    public async Task<HealthOfficerEscalationReviewContext?> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!options.Enabled || !options.EscalationApprovalsEnabled || !options.EscalationAssignmentsEnabled || caseId == Guid.Empty) return null;
        ICaseCommandLease lease;
        try { lease = await unitOfWork.BeginCaseAsync(actor, caseId, [], CaseLeaseMode.Read, ct).ConfigureAwait(false); }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.") { return null; }
        await using (lease.ConfigureAwait(false))
        {
            var decision = await policy.AuthorizeAllAsync(actor, caseId,
                [CaseOperation.ReadCase, CaseOperation.ManageEscalation, CaseOperation.ManageFollowUp], ct).ConfigureAwait(false);
            if (!decision.IsAllowed) return null;
            var data = await store.GetAsync(lease, caseId, ct).ConfigureAwait(false);
            if (data is null) return null;
            var candidates = await officers.GetAsync(caseId, 20, ct).ConfigureAwait(false);
            await lease.CommitAsync(ct).ConfigureAwait(false);
            return new(data.CaseVersion, data.Escalations, data.HasMore, candidates.Items, candidates.HasMore);
        }
    }
}
