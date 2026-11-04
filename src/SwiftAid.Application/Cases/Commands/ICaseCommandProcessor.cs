using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Application.Cases.Commands;

/// <summary>Processes a non-creation command for one authenticated, server-derived actor.</summary>
public interface ICaseCommandProcessor
{
    ValueTask<CaseCommandProcessingResult> ProcessAsync(
        ActorContext actor,
        Guid caseId,
        int wireCommandSchemaVersion,
        CaseCommand command,
        CancellationToken cancellationToken);
}
