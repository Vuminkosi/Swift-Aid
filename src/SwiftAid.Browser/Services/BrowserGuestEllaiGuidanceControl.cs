using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Browser.Services;

/// <summary>The public guest client currently exposes public product help
/// only. Generated application navigation remains unavailable to guests.</summary>
public sealed class BrowserGuestEllaiGuidanceControl : IEllaiGuidanceControl
{
    public Task<GuidanceSnapshot> StartAsync(
        string journeyId, string journeyVersion, GuidanceMode mode,
        string? startStepId, CancellationToken cancellationToken,
        string? clientCommandId = null) =>
        Task.FromException<GuidanceSnapshot>(new NotSupportedException(
            "Guest Ellai is limited to public product help."));

    public Task<GuidanceSnapshot?> DispatchAsync(
        string attemptId, long expectedVersion, GuidanceCommandKind command,
        GuidanceMode mode, string? targetStepId, GuidanceMode? targetMode,
        CancellationToken cancellationToken,
        string? clientCommandId = null) => Task.FromResult<GuidanceSnapshot?>(null);

    public Task<GuidanceSnapshot?> StopAsync(
        string attemptId, long expectedVersion, GuidanceMode mode,
        CancellationToken cancellationToken,
        string? clientCommandId = null) => Task.FromResult<GuidanceSnapshot?>(null);
}
