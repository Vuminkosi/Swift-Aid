namespace SwiftAid.Application.Ellai;

/// <summary>Application-owned activity events that drive composer status.
/// Published by the trusted runtime (cycle runner, broker pipeline, approval
/// and guidance services) — never by the model. Carries only routing stamps
/// and safe context keys; no record content, arguments or chain-of-thought.</summary>
public enum EllaiActivityKind
{
    OperationAccepted,
    ToolRequested,
    ToolStarted,
    ToolObservationAccepted,
    ReasoningTurnStarted,
    ApprovalRequired,
    UiTransitionPending,
    VerificationStarted,
    CycleCompleted,
    CycleBlocked,
    SessionCancelled,
    ToolPending
}

/// <summary>One correlated activity event. Detail is a safe context key
/// (e.g. docs, read, ui, denied, budget) selected by the publisher from
/// the template table, not free text.</summary>
public sealed record EllaiActivityEvent(
    string SessionId,
    long ControllerEpoch,
    long Sequence,
    EllaiActivityKind Kind,
    string CycleId,
    string Detail,
    DateTimeOffset AtUtc,
    string? ToolName = null);

/// <summary>Projected composer status: deterministic template text, never a
/// claim about tool success. Rendered beneath the single compact composer.</summary>
public sealed record EllaiActivityStatus(
    string SessionId,
    string Phase,
    string Text,
    long Sequence,
    bool Terminal);

/// <summary>Projects real correlated session/cycle/tool/approval events into
/// the transient status beneath the compact composer (§8.4). Deterministic
/// template table — no LLM call manufactures progress phrases. Late,
/// duplicate, wrong-controller and post-cancel events are fenced out; a
/// terminal error, timeout, budget stop or cancellation projects its safe
/// reason instead of hanging on an activity label.</summary>
public sealed class EllaiActivityStatusCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionProjection> _sessions = new(StringComparer.Ordinal);

    private sealed class SessionProjection
    {
        public long ControllerEpoch;
        public long LastSequence;
        public bool Cancelled;
        public EllaiActivityStatus? Current;
    }

    /// <summary>Publishes one trusted event. Returns the new projected
    /// status, or null when the event was fenced (stale, duplicate,
    /// wrong controller, or post-cancel).</summary>
    public EllaiActivityStatus? Publish(EllaiActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentException.ThrowIfNullOrWhiteSpace(activity.SessionId);

        lock (_gate)
        {
            if (!_sessions.TryGetValue(activity.SessionId, out var projection))
            {
                projection = new SessionProjection { ControllerEpoch = activity.ControllerEpoch };
                _sessions[activity.SessionId] = projection;
            }

            if (projection.Cancelled)
            {
                return null;
            }

            if (activity.ControllerEpoch < projection.ControllerEpoch
                || (activity.ControllerEpoch == projection.ControllerEpoch
                    && activity.Sequence <= projection.LastSequence))
            {
                return null;
            }

            projection.ControllerEpoch = activity.ControllerEpoch;
            projection.LastSequence = activity.Sequence;
            if (activity.Kind == EllaiActivityKind.SessionCancelled)
            {
                projection.Cancelled = true;
            }

            projection.Current = Project(activity);
            return projection.Current;
        }
    }

    /// <summary>Current status for the session under the calling controller
    /// epoch. Unknown sessions and epoch mismatches yield null so old
    /// cards and retired controllers never overwrite new state.</summary>
    public EllaiActivityStatus? CurrentStatus(string sessionId, long controllerEpoch)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var projection)
                || projection.ControllerEpoch != controllerEpoch)
            {
                return null;
            }

            return projection.Current;
        }
    }

    private static EllaiActivityStatus Project(EllaiActivityEvent activity)
    {
        var (phase, text, terminal) = activity.Kind switch
        {
            EllaiActivityKind.OperationAccepted => ("working", "Checking that for you…", false),
            EllaiActivityKind.ToolRequested => ("working", "Checking that for you…", false),
            EllaiActivityKind.ToolStarted => ToolStartedText(activity.Detail),
            EllaiActivityKind.ToolObservationAccepted => ("reviewing", "Reviewing the evidence…", false),
            EllaiActivityKind.ReasoningTurnStarted => ("reviewing", "Reviewing the evidence…", false),
            EllaiActivityKind.ApprovalRequired => ("approval", "Waiting for your approval…", false),
            EllaiActivityKind.UiTransitionPending => ("opening", "Opening the permitted view…", false),
            EllaiActivityKind.VerificationStarted => ("verifying", "Checking the result…", false),
            EllaiActivityKind.CycleCompleted => CompletionText(activity.Detail),
            EllaiActivityKind.CycleBlocked => ("blocked", BlockedText(activity.Detail), true),
            EllaiActivityKind.SessionCancelled => ("cancelled", "Stopped.", true),
            _ => ("working", "Checking that for you…", false),
        };

        return new EllaiActivityStatus(activity.SessionId, phase, text, activity.Sequence, terminal);
    }

    private static (string Phase, string Text, bool Terminal) ToolStartedText(string detail) =>
        detail switch
        {
            "docs" => ("reading", "Checking SwiftAid's technical reference…", false),
            "ui" or "guidance" => ("reading", "Checking this screen…", false),
            "receipt" => ("verifying", "Checking the result…", false),
            _ => ("reading", "Checking the latest recorded information…", false),
        };

    private static (string Phase, string Text, bool Terminal) CompletionText(string detail) =>
        detail switch
        {
            "verified" => ("done", "Done — the result is verified.", true),
            "empty" => ("done", "No matching records were available to me.", true),
            _ => ("blocked", "I found information, but need another authorized step to finish reviewing it.", true),
        };

    private static string BlockedText(string detail) =>
        detail switch
        {
            "denied" => "I can't check that with the current permissions.",
            "unavailable" => "That information isn't available right now.",
            "budget" => "The trusted budget for this request is used up.",
            "iterations" => "Ellai reached this request's reasoning limit before it could finish. Try a narrower question.",
            "timeout" => "Still checking; the connection is taking longer than expected…",
            "unreviewed" => "I found information, but need another authorized step to finish reviewing it.",
            _ => "Checking stopped before finishing.",
        };
}
