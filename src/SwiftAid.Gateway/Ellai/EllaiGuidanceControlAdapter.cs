using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Bridges the UI-owned guidance control seam to the application
/// coordinator. The presentation layer never touches coordinator internals
/// or the library session directly. Callers supply stable client command
/// ids so a commit-then-timeout retry reuses the same identity instead of
/// minting a fresh one; the coordinator deduplicates repeats.</summary>
public sealed class EllaiGuidanceControlAdapter(EllaiGuidanceSessionCoordinator coordinator)
    : IEllaiGuidanceControl
{
    public Task<GuidanceSnapshot> StartAsync(
        string journeyId, string journeyVersion, GuidanceMode mode,
        string? startStepId, CancellationToken cancellationToken,
        string? clientCommandId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyVersion);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(coordinator.Start(new StartGuidanceRequest(
            clientCommandId ?? $"cmd-{Guid.NewGuid():N}", journeyId, journeyVersion, mode, startStepId)));
    }

    public Task<GuidanceSnapshot?> DispatchAsync(
        string attemptId, long expectedVersion, GuidanceCommandKind command,
        GuidanceMode mode, string? targetStepId, GuidanceMode? targetMode,
        CancellationToken cancellationToken,
        string? clientCommandId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(coordinator.Dispatch(
            new GuidanceControlRequest(
                clientCommandId ?? $"cmd-{Guid.NewGuid():N}", attemptId, expectedVersion,
                command, targetStepId, targetMode),
            mode));
    }

    public Task<GuidanceSnapshot?> StopAsync(
        string attemptId, long expectedVersion, GuidanceMode mode, CancellationToken cancellationToken,
        string? clientCommandId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(coordinator.Dispatch(
            new GuidanceControlRequest(
                clientCommandId ?? $"cancel-{Guid.NewGuid():N}", attemptId, expectedVersion,
                GuidanceCommandKind.Stop, null, null),
            mode));
    }
}
