using System.Collections.Immutable;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Cases;

/// <summary>Returns case identifiers only; authorization happens before identifiers are exposed.</summary>
public interface ICaseWorkDiscoveryStore
{
    Task<IReadOnlyList<Guid>> GetCasesAfterAsync(CaseWorkDiscoveryKind kind, string? assignedActorId,
        Guid? after, int take, CancellationToken cancellationToken);
}

public interface ICaseWorkDiscoveryQuery
{
    Task<CaseWorkDiscoveryPage> GetAsync(ActorContext actor, CaseWorkDiscoveryKind kind,
        CaseWorkDiscoveryScope scope, int limit, Guid? cursor, CancellationToken cancellationToken);
}

public sealed class CaseWorkDiscoveryQuery(ICaseWorkDiscoveryStore store, ICaseAccessPolicy policy,
    ICaseCommandUnitOfWork unitOfWork) : ICaseWorkDiscoveryQuery
{
    public async Task<CaseWorkDiscoveryPage> GetAsync(ActorContext actor, CaseWorkDiscoveryKind kind,
        CaseWorkDiscoveryScope scope, int limit, Guid? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(scope) || limit is < 1 or > 100 || cursor == Guid.Empty ||
            kind == CaseWorkDiscoveryKind.Escalation && scope == CaseWorkDiscoveryScope.Assigned)
            throw new ArgumentException("The case work discovery request is invalid.");

        var items = new List<Guid>(limit);
        var seen = new HashSet<Guid>();
        var after = cursor;
        try
        {
            while (true)
            {
                var candidates = await store.GetCasesAfterAsync(kind,
                    scope == CaseWorkDiscoveryScope.Assigned ? actor.ActorId : null,
                    after, 200, cancellationToken).ConfigureAwait(false);
                if (candidates is null || candidates.Count > 200)
                    return new(CaseQueueAccess.Unavailable, []);
                foreach (var caseId in candidates)
                {
                    if (caseId == Guid.Empty || caseId == after || !seen.Add(caseId))
                        return new(CaseQueueAccess.Unavailable, []);
                    after = caseId;
                    ICaseCommandLease lease;
                    try
                    {
                        lease = await unitOfWork.BeginCaseAsync(actor, caseId, ImmutableArray<string>.Empty,
                            CaseLeaseMode.Read, cancellationToken).ConfigureAwait(false);
                    }
                    catch (KeyNotFoundException) { continue; }
                    await using (lease.ConfigureAwait(false))
                    {
                        var decision = await policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, cancellationToken).ConfigureAwait(false);
                        if (!decision.IsAllowed) continue;
                        await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
                        if (items.Count == limit) return new(CaseQueueAccess.Allowed, items, items[^1]);
                        items.Add(caseId);
                    }
                }
                if (candidates.Count < 200) return new(CaseQueueAccess.Allowed, items);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(CaseQueueAccess.Unavailable, []); }
    }
}
