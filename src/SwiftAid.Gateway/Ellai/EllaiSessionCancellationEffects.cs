using SwiftAid.Application.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Applies the shared session cancellation projections after the
/// durable owner/version transition succeeds.</summary>
public static class EllaiSessionCancellationEffects
{
    public static void Apply(
        string sessionId,
        DateTimeOffset atUtc,
        EllaiApprovalDialogCoordinator? dialogs,
        EllaiSessionEventJournal? journals,
        EllaiSessionResponseStore? responses = null)
    {
        dialogs?.CancelSession(sessionId, atUtc);
        journals?.AppendOnce(sessionId, "session-cancelled", null, "Stopped.", atUtc);
        responses?.Clear(sessionId);
    }
}
