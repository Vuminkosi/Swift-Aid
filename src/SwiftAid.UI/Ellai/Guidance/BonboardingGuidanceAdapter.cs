using Bit.Bonboarding;

namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>Allowed guidance-session commands. Creation uses the service
/// StartAsync overloads; completion, signalling, checklist writes and fixture
/// resets are never exposed here — only the deterministic coordinator
/// concludes a journey after its declared gates.</summary>
public enum EllaiGuidanceCommand
{
    Next,
    Previous,
    GoTo,
    Skip,
    Pause,
    Resume,
    Refresh,
    Dismiss,
    Cancel
}

/// <summary>Presentation adapter over the single library session. Owns no
/// investigation, permission or business-proof semantics; it only forwards
/// the coordinator-authorized navigation subset to
/// <see cref="IBonboardingTourSession"/>.</summary>
public sealed class BonboardingGuidanceAdapter(IBonboardingTourSession session)
{
    public Task DispatchAsync(EllaiGuidanceCommand command, string? stepId, CancellationToken ct) =>
        command switch
        {
            EllaiGuidanceCommand.Next => session.NextAsync(ct),
            EllaiGuidanceCommand.Previous => session.PreviousAsync(ct),
            EllaiGuidanceCommand.GoTo when stepId is not null => session.GoToAsync(stepId, ct),
            EllaiGuidanceCommand.Skip => session.SkipAsync(ct),
            EllaiGuidanceCommand.Pause => session.PauseAsync(ct),
            EllaiGuidanceCommand.Resume => session.ResumeAsync(ct),
            EllaiGuidanceCommand.Refresh => session.RefreshAsync(ct),
            EllaiGuidanceCommand.Dismiss => session.DismissAsync(ct),
            EllaiGuidanceCommand.Cancel => session.CancelAsync(ct),
            _ => throw new ArgumentException($"Unsupported guidance command {command}.", nameof(command))
        };
}
