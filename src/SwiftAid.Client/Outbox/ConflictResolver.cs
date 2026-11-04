using SwiftAid.Client.Outbox;

namespace SwiftAid.Client.Outbox;

/// <summary>Builds a fresh proposal from an explicit human reconciliation of a retained conflict.</summary>
public static class ConflictResolver
{
    public static LocalCommandProposal CreateReconciledProposal(
        LocalCommandConflict conflict,
        Guid newCommandId,
        string commandType,
        byte[] reconciledPayload)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentNullException.ThrowIfNull(reconciledPayload);
        if (newCommandId == Guid.Empty || newCommandId == conflict.CommandId)
        {
            throw new ArgumentException("A reconciled proposal requires a new command identifier.", nameof(newCommandId));
        }

        if (string.IsNullOrWhiteSpace(commandType) || reconciledPayload.Length == 0)
        {
            throw new ArgumentException("A command type and reconciled payload are required.");
        }

        return new LocalCommandProposal(
            newCommandId,
            conflict.CaseId,
            conflict.WorkspaceId,
            commandType,
            conflict.LatestAggregateVersion,
            DependsOnCommandId: null,
            reconciledPayload.ToArray());
    }
}
