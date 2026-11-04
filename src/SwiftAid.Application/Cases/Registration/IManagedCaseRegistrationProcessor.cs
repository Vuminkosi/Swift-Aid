using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Cases.Registration;

public interface IManagedCaseRegistrationProcessor
{
    ValueTask<CaseRegistrationResult> RegisterAsync(
        ActorContext actor,
        RegisterManagedCaseRequest request,
        CancellationToken cancellationToken);
}
