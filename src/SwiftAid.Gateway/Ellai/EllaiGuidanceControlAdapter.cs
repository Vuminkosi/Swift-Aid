using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Bridges the UI-owned guidance control seam to the application
/// coordinator. The presentation layer never touches coordinator internals
/// or the library session directly.</summary>
public sealed class EllaiGuidanceControlAdapter(EllaiGuidanceSessionCoordinator coordinator)
    : IEllaiGuidanceControl
{
    public Task<GuidanceSnapshot> StartAsync(
        string journeyId, string journeyVersion, GuidanceMode mode,
        string? startStepId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyVersion);
        return Task.FromResult(coordinator.Start(new StartGuidanceRequest(
            $"cmd-{Guid.NewGuid():N}", journeyId, journeyVersion, mode, startStepId)));
    }

    public Task<GuidanceSnapshot?> DispatchAsync(
        string attemptId, long expectedVersion, GuidanceCommandKind command,
        GuidanceMode mode, string? targetStepId, GuidanceMode? targetMode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        return Task.FromResult(coordinator.Dispatch(
            new GuidanceControlRequest(
                $"cmd-{Guid.NewGuid():N}", attemptId, expectedVersion,
                command, targetStepId, targetMode),
            mode));
    }

    public Task<GuidanceSnapshot?> StopAsync(
        string attemptId, long expectedVersion, GuidanceMode mode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        return Task.FromResult(coordinator.Dispatch(
            new GuidanceControlRequest(
                $"cancel-{Guid.NewGuid():N}", attemptId, expectedVersion,
                GuidanceCommandKind.Stop, null, null),
            mode));
    }
}
