using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Cases.Registration;

public interface ICaseRegistrationUnitOfWork
{
    ValueTask<ICaseRegistrationLease> BeginCreateAsync(
        ActorContext actor,
        string workspaceId,
        CancellationToken cancellationToken);

    ValueTask<ICaseRegistrationLease> BeginExistingCaseReplayAsync(
        ActorContext actor,
        Guid candidateCaseId,
        CancellationToken cancellationToken);
}
