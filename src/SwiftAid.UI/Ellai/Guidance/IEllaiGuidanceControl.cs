using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>UI-owned guidance control seam. Implemented by the hosting app
/// (which delegates to the application coordinator); the presentation layer
/// never drives the library session directly. Terminal attempts reject
/// every command; stale versions yield Stale, never execution.</summary>
public interface IEllaiGuidanceControl
{
    Task<GuidanceSnapshot> StartAsync(
        string journeyId, string journeyVersion, GuidanceMode mode,
        string? startStepId, CancellationToken cancellationToken,
        string? clientCommandId = null);

    Task<GuidanceSnapshot?> DispatchAsync(
        string attemptId, long expectedVersion, GuidanceCommandKind command,
        GuidanceMode mode, string? targetStepId, GuidanceMode? targetMode,
        CancellationToken cancellationToken,
        string? clientCommandId = null);

    Task<GuidanceSnapshot?> StopAsync(
        string attemptId, long expectedVersion, GuidanceMode mode, CancellationToken cancellationToken,
        string? clientCommandId = null);
}
