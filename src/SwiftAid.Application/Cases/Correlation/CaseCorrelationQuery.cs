using System.Collections.Immutable;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Cases.Correlation;

public sealed record StoredCorrelationCandidate(Guid CandidateId, Guid FirstCaseId, Guid SecondCaseId,
    long FirstRevision, long SecondRevision, DateTimeOffset RecordedAtUtc, string[] Reasons);

public interface ICaseCorrelationStore
{
    Task<IReadOnlyList<StoredCorrelationCandidate>> GetCandidatesAsync(Guid caseId, int limit, CancellationToken ct);
    Task<bool> AreCurrentAsync(StoredCorrelationCandidate candidate, CancellationToken ct);
}

/// <summary>Returns only candidates for which both current case scopes grant ReadCase.</summary>
public sealed class CaseCorrelationQuery(ICaseCorrelationStore store, ICaseCommandUnitOfWork unitOfWork,
    ICaseAccessPolicy policy, IEllaiCaseAuthScopeFactory scopes)
{
    public async Task<CaseCorrelationReadResult> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (caseId == Guid.Empty) return new(false, []);
        ICaseCommandLease source;
        try { source = await unitOfWork.BeginCaseAsync(actor, caseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct); }
        catch (KeyNotFoundException) { return new(false, []); }
        catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.")
        { return new(false, []); }
        await using (source.ConfigureAwait(false))
        {
            if (!(await policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct)).IsAllowed) return new(false, []);
            var candidates = await store.GetCandidatesAsync(caseId, 100, ct);
            var visible = new List<CaseCorrelationView>();
            foreach (var candidate in candidates)
            {
                var other = candidate.FirstCaseId == caseId ? candidate.SecondCaseId : candidate.FirstCaseId;
                await using var scope = scopes.CreateScope();
                ICaseCommandLease target;
                try { target = await scope.UnitOfWork.BeginCaseAsync(actor, other, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct); }
                catch (KeyNotFoundException) { continue; }
                catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.") { continue; }
                await using (target.ConfigureAwait(false))
                {
                    if (!(await scope.Policy.AuthorizeAsync(actor, other, CaseOperation.ReadCase, ct)).IsAllowed ||
                        !await store.AreCurrentAsync(candidate, ct)) continue;
                    visible.Add(new(candidate.CandidateId, other, candidate.RecordedAtUtc, candidate.Reasons,
                        "Possible relationship only; shared attributes do not establish identity or causation."));
                    await target.CommitAsync(ct);
                }
            }
            await source.CommitAsync(ct);
            return new(true, visible);
        }
    }
}
