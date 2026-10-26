using System.Collections.Immutable;
using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Cases.Commands;

/// <summary>Begins a case-bound transaction and acquires its authorization locks.</summary>
public interface ICaseCommandUnitOfWork
{
    ValueTask<ICaseCommandLease> BeginCaseAsync(
        ActorContext actor,
        Guid caseId,
        ImmutableArray<string> additionalActorHints,
        CaseLeaseMode mode,
        CancellationToken cancellationToken);
}
