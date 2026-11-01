using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Formats trusted tool lifecycle events for the existing transient feed.
/// It never executes tools, invokes TTS, grants approval, or writes conversation messages.</summary>
public sealed class EllaiToolProgressPublisher(
    EllaiSessionResponseStore responses, string sessionId, string utteranceId,
    long controllerEpoch, int attempt, TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private bool _hasTools;
    private bool _finished;
    private int _sequence;
    private int _spokenUpdates;
    private long _lastRuntimeSequence;
    private DateTimeOffset _lastSpeech;
    private string _operation = "Checking that for you";

    public void Observe(EllaiActivityEvent activity)
    {
        lock (_gate)
        {
            if (_finished || activity.SessionId != sessionId || activity.ControllerEpoch != controllerEpoch
                || activity.Sequence <= _lastRuntimeSequence) return;
            _lastRuntimeSequence = activity.Sequence;
            if (activity.Kind == EllaiActivityKind.ToolStarted)
            {
                var (operation, acknowledgement) = Describe(activity.ToolName, utteranceId);
                _operation = operation;
                if (!_hasTools)
                {
                    _hasTools = true;
                    // A recovered outbox attempt must not repeat an initial acknowledgement,
                    // including after a Gateway restart that erased transient delivery state.
                    Publish(attempt == 1 ? "Acknowledgement" : "Progress", operation,
                        attempt == 1 ? acknowledgement : string.Empty, attempt == 1, "initial");
                }
                else Publish("Progress", operation);
            }
            else if (activity.Kind == EllaiActivityKind.ToolPending && _hasTools)
            {
                var now = _clock.GetUtcNow();
                // ToolPending already proves the runner's initial wait elapsed.
                // Rate-limit only subsequent callbacks, including parallel tools.
                if (_spokenUpdates < 2 && (_spokenUpdates == 0 || now - _lastSpeech >= TimeSpan.FromSeconds(15)))
                {
                    _spokenUpdates++;
                    Publish("Progress", _operation, _spokenUpdates == 1
                        ? "That check is taking a little longer. I'm still waiting for the result."
                        : "The check is still running. You can keep waiting or cancel.", true);
                }
            }
            else if (activity.Kind == EllaiActivityKind.ToolObservationAccepted && _hasTools)
                Publish("Progress", "Reviewing the result");
            else if (activity.Kind == EllaiActivityKind.ApprovalRequired)
                Publish("ApprovalRequired", "Waiting for your approval");
        }
    }

    public void Finish(string outcome)
    {
        lock (_gate)
        {
            if (_finished || !_hasTools) return;
            _finished = true;
            var (state, text) = outcome switch
            {
                "Completed" => ("Completed", "Finished"),
                "Cancelled" => ("Cancelled", "Request cancelled"),
                "Interrupted" => ("Interrupted", "Response interrupted"),
                "RetryableFailure" => ("Retrying", "Reconnecting…"),
                _ => ("Failed", "Couldn't complete that request"),
            };
            Publish(state, text);
        }
    }

    private void Publish(string state, string operation, string text = "", bool speak = false, string? key = null)
    {
        var now = _clock.GetUtcNow();
        var id = key == "initial" ? $"{utteranceId}:initial"
            : $"{utteranceId}:{attempt}:{++_sequence}";
        if (speak) _lastSpeech = now;
        responses.PublishProgress(sessionId, utteranceId, text,
            new(id, state, operation, speak, controllerEpoch, now.AddSeconds(speak ? 8 : 30)));
    }

    // English is the only validated catalogue. ss/sn deliberately fall back to
    // English until translations and the corresponding MMS voice are validated.
    public static (string Operation, string Acknowledgement) Describe(string? toolName, string turnId, string language = "en") => toolName switch
    {
        EllaiToolNames.State or EllaiToolNames.Evidence => ("Checking the records", "Let me check the records."),
        EllaiToolNames.DocsSearch or EllaiToolNames.DocsGet => ("Checking the reference", "Let me look that up."),
        EllaiToolNames.ProductFacts => ("Checking SwiftAid information", "Let me find out about that."),
        EllaiToolNames.Receipts => ("Checking the action result", "I'll check what happened."),
        EllaiToolNames.UiCatalogue or EllaiToolNames.UiObserve => ("Checking the current screen", "Let me check this screen."),
        EllaiToolNames.Now => ("Checking the current time", "One moment."),
        _ => ("Checking that for you", turnId.Length % 2 == 0 ? "Hmm, let me check that." : "One moment."),
    };
}
