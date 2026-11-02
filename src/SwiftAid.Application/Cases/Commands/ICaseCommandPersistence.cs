using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases.Commands;

/// <summary>Persistence operations used by the managed-case command processor.</summary>
/// <remarks>
/// Every operation is scoped to the same active case lease. Implementations must reject a
/// lease that is inactive, for another case, or in the wrong mode. Staging methods only
/// mutate the lease-owned unit of work; <see cref="ICaseCommandLease.CommitAsync"/> is the
/// sole persistence commit point.
/// </remarks>
public interface ICaseCommandPersistence
{
    /// <summary>Reads the current projection header after preliminary authorization succeeds.</summary>
    ValueTask<CaseCommandCaseHeader?> ReadCaseHeaderAsync(
        ICaseCommandLease lease,
        CancellationToken cancellationToken);

    /// <summary>Strictly reads and replays the complete, ordered event journal under the lease.</summary>
    ValueTask<CaseAggregate> ReplayCaseAsync(
        ICaseCommandLease lease,
        CancellationToken cancellationToken);

    /// <summary>Finds an accepted receipt by its actor and command key inside the lease transaction.</summary>
    ValueTask<CaseCommandReceipt?> FindReceiptAsync(
        ICaseCommandLease lease,
        string actorId,
        Guid commandId,
        CancellationToken cancellationToken);

    /// <summary>Reads the database clock as UTC for server-recorded event timestamps.</summary>
    ValueTask<DateTimeOffset> GetDatabaseUtcNowAsync(
        ICaseCommandLease lease,
        CancellationToken cancellationToken);

    /// <summary>
    /// Checks a proposed accountable owner against the current active actor, access-state,
    /// same-scope membership, and a matching active grant containing ReadCase. This check does
    /// not require prior case assignment/share or regional coverage. Call only for a new command
    /// after receipt replay has been ruled out.
    /// </summary>
    ValueTask<bool> IsEligibleAssignmentTargetAsync(
        ICaseCommandLease lease,
        string targetActorId,
        CaseCommandCaseHeader caseHeader,
        CancellationToken cancellationToken);

    /// <summary>Checks a locked collaborator against the case scope and all requested actions in one active grant group.</summary>
    ValueTask<bool> IsEligibleCollaboratorAsync(
        ICaseCommandLease lease,
        string targetActorId,
        CaseCollaborationActions actions,
        CaseCommandCaseHeader caseHeader,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stages protected payloads, metadata events, the supported projection changes, an accepted
    /// receipt, minimized audit data, and outbox references without saving or committing.
    /// Implementations must repeat the authoritative scope, group, operation, membership, and
    /// version checks in the same database transaction before staging the write.
    /// </summary>
    ValueTask<CaseCommandAppendOutcome> StageAcceptedCommandAsync(
        ICaseCommandLease lease,
        CaseCommandAppendRequest request,
        CancellationToken cancellationToken);

    /// <summary>Stages only a minimized denial audit record; it never stores reason or narrative.</summary>
    ValueTask StageDeniedAuditAsync(
        ICaseCommandLease lease,
        CaseCommandDenialAudit audit,
        CancellationToken cancellationToken);
}
