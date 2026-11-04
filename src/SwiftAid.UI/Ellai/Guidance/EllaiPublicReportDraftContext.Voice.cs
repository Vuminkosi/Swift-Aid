using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.UI.Ellai.Guidance;

public sealed partial class EllaiPublicReportDraftContext
{
    private object? _voiceController;
    private object? _voiceReportIdentity;
    private Func<ReportVoiceSuggestion, string, bool>? _autoFillVoice;
    private Func<EllaiVoiceCaptureMode, Task<bool>>? _startVoice;
    private Func<Task>? _stopVoice;
    private Func<Func<CancellationToken, Task<byte[]>>, string, CancellationToken, Task<bool>>? _savedVoice;
    private readonly HashSet<string> _voiceTurns = new(StringComparer.Ordinal);
    private long _voiceGeneration;
    private bool _voiceEnabled;
    private bool _voiceRecording;
    private string? _voiceCaptureId;
    private CancellationTokenSource? _voiceLifetime;
    private string _voiceStatus = "Choose a voice mode to help fill this report.";
    private IReadOnlyList<ReportVoiceSuggestion> _voiceSuggestions = [];

    public event Action? VoiceChanged;
    public bool VoiceEnabled { get { lock (_gate) return _voiceEnabled; } }
    public bool VoiceRecording { get { lock (_gate) return _voiceRecording; } }
    public bool VoiceAvailable { get { lock (_gate) return _owner is not null && _startVoice is not null; } }
    public string VoiceStatus { get { lock (_gate) return _voiceStatus; } }
    public IReadOnlyList<ReportVoiceSuggestion> VoiceSuggestions { get { lock (_gate) return _voiceSuggestions; } }

    public void RegisterVoiceController(object controller, Func<EllaiVoiceCaptureMode, Task<bool>> start, Func<Task> stop,
        Func<Func<CancellationToken, Task<byte[]>>, string, CancellationToken, Task<bool>>? savedVoice = null)
    {
        lock (_gate) { _voiceController = controller; _startVoice = start; _stopVoice = stop; _savedVoice = savedVoice; }
        VoiceChanged?.Invoke();
    }

    public void RemoveVoiceController(object controller)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_voiceController, controller)) return;
            _voiceController = null;
            _startVoice = null;
            _stopVoice = null;
            _savedVoice = null;
            ResetVoiceAssistanceLocked();
        }
        VoiceChanged?.Invoke();
    }

    /// <summary>Called only after an explicit processing choice in the report form.</summary>
    public async Task<bool> StartReportVoiceAsync(EllaiVoiceCaptureMode mode)
    {
        Func<EllaiVoiceCaptureMode, Task<bool>>? start;
        long generation;
        lock (_gate)
        {
            if (_owner is null || _startVoice is null) return false;
            _voiceLifetime?.Cancel();
            _voiceLifetime?.Dispose();
            _voiceLifetime = new CancellationTokenSource();
            _voiceEnabled = true;
            _voiceCaptureId = null;
            generation = ++_voiceGeneration;
            _voiceTurns.Clear();
            _voiceSuggestions = [];
            _voiceStatus = "Starting voice capture…";
            start = _startVoice;
        }
        VoiceChanged?.Invoke();
        bool started;
        try { started = await start(mode).ConfigureAwait(false); }
        catch (Exception) { started = false; }
        Func<Task>? stopStale = null;
        var stale = false;
        lock (_gate)
        {
            if (_voiceGeneration != generation)
            {
                stale = true;
                stopStale = started ? _stopVoice : null;
            }
            else
            {
                if (!started) _voiceEnabled = false;
                _voiceRecording = started;
                _voiceStatus = started ? "Listening. Review the transcript and suggested details before sending." :
                    "Voice assistance is unavailable. You can continue typing.";
            }
        }
        if (stopStale is not null) await stopStale().ConfigureAwait(false);
        if (stale) return false;
        VoiceChanged?.Invoke();
        return started;
    }

    public async Task<bool> TranscribeSavedVoiceAsync(Func<CancellationToken, Task<byte[]>> readWave)
    {
        Func<Func<CancellationToken, Task<byte[]>>, string, CancellationToken, Task<bool>> run;
        string captureId;
        long generation;
        CancellationToken token;
        lock (_gate)
        {
            if (_owner is null || _savedVoice is null || _voiceRecording) return false;
            _voiceLifetime?.Cancel();
            _voiceLifetime?.Dispose();
            _voiceLifetime = new CancellationTokenSource();
            token = _voiceLifetime.Token;
            _voiceEnabled = true;
            _voiceTurns.Clear();
            _voiceSuggestions = [];
            captureId = Guid.NewGuid().ToString("N");
            _voiceCaptureId = captureId;
            generation = ++_voiceGeneration;
            _voiceStatus = "Transcribing the saved voice note...";
            run = _savedVoice;
        }
        VoiceChanged?.Invoke();
        bool success;
        try { success = await run(readWave, captureId, token).ConfigureAwait(false); }
        catch (Exception) { success = false; }
        lock (_gate)
        {
            if (generation != _voiceGeneration) return false;
            if (!success) _voiceStatus = "Saved voice transcription is unavailable. You can retry or type your report.";
        }
        VoiceChanged?.Invoke();
        return success;
    }

    public async Task StopReportVoiceAsync()
    {
        Func<Task>? stop;
        lock (_gate) { stop = _stopVoice; _voiceStatus = "Finishing voice capture…"; }
        VoiceChanged?.Invoke();
        if (stop is not null) await stop().ConfigureAwait(false);
        lock (_gate) { _voiceRecording = false; _voiceCaptureId = null; }
        VoiceChanged?.Invoke();
    }

    public void MarkVoiceStopped(bool failed = false)
    {
        lock (_gate)
        {
            _voiceRecording = false;
            _voiceCaptureId = null;
            if (failed) _voiceStatus = "Voice capture stopped. You can retry or continue typing.";
            else if (_voiceStatus == "Finishing voice capture…")
                _voiceStatus = "Voice capture stopped. If no transcript appears, retry or continue typing.";
        }
        VoiceChanged?.Invoke();
    }

    public void EndReportVoiceAssistance()
    {
        lock (_gate) ResetVoiceAssistanceLocked();
        VoiceChanged?.Invoke();
    }

    public void BindVoiceCapture(string captureId)
    {
        lock (_gate)
        {
            if (_voiceEnabled && _owner is not null) _voiceCaptureId = captureId;
        }
    }

    public ReportVoiceTurnLease? AdmitVoiceTurn(string sessionId, string turnId, string? captureId)
    {
        lock (_gate)
        {
            if (!_voiceEnabled || _owner is null || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(turnId) ||
                string.IsNullOrWhiteSpace(captureId) || !string.Equals(_voiceCaptureId, captureId, StringComparison.Ordinal) ||
                !_voiceTurns.Add(sessionId + ":" + turnId)) return null;
            _voiceStatus = "Processing the latest transcript…";
            return new(_voiceGeneration, _voiceLifetime?.Token ?? CancellationToken.None);
        }
    }

    public bool AppendVoiceTranscript(long generation, string transcript)
    {
        bool saved;
        lock (_gate)
        {
            if (!_voiceEnabled || generation != _voiceGeneration || _owner is null) return false;
            saved = _appendNarrative?.Invoke(transcript) == true;
            _voiceStatus = saved ? "Transcript added. Finding suggested details…" :
                "The transcript could not be added. Enter it in the report instead.";
        }
        VoiceChanged?.Invoke();
        return saved;
    }

    public void CompleteVoiceTurn(long generation, string transcript, ReportVoiceExtractionResponse? response)
    {
        lock (_gate)
        {
            if (!_voiceEnabled || generation != _voiceGeneration || _owner is null) return;
            if (response?.Status == "Ready")
            {
                foreach (var suggestion in response.Suggestions)
                {
                    // Only a single proposal per field may be auto-applied; competing values remain suggestions.
                    if (response.Suggestions.Count(item => item.Field == suggestion.Field) == 1)
                        _autoFillVoice?.Invoke(suggestion, transcript);
                }
                // Show and apply the whole finalized utterance so a short model excerpt
                // cannot hide nearby negation or uncertainty from the reviewer.
                _voiceSuggestions = _voiceSuggestions.Concat(response.Suggestions
                    .Select(item => item with { Evidence = transcript })).TakeLast(24).ToArray();
            }
            _voiceStatus = response?.Status == "Ready" ? "Transcript added. Review it and the suggested details before sending." :
                "Transcript added; detail suggestions are unavailable. Review the report before sending.";
        }
        VoiceChanged?.Invoke();
    }

    public bool ApplyVoiceSuggestion(ReportVoiceSuggestion suggestion)
    {
        bool applied;
        lock (_gate)
        {
            if (_owner is null || !_voiceSuggestions.Contains(suggestion)) return false;
            applied = suggestion.Field switch
            {
                "age" when int.TryParse(suggestion.Value, out var age) && age >= 0 => _setAge?.Invoke(age) == true,
                "locality" => _setLocality?.Invoke(suggestion.Value) == true,
                "observation" => _addObservation?.Invoke(suggestion.Evidence) == true,
                _ => false
            };
            if (applied) _voiceSuggestions = _voiceSuggestions.Where(item => !ReferenceEquals(item, suggestion)).ToArray();
        }
        if (applied) VoiceChanged?.Invoke();
        return applied;
    }

    private void ResetVoiceAssistanceLocked()
    {
        _voiceGeneration++;
        _voiceLifetime?.Cancel();
        _voiceLifetime?.Dispose();
        _voiceLifetime = null;
        _voiceEnabled = false;
        _voiceRecording = false;
        _voiceCaptureId = null;
        _voiceTurns.Clear();
        _voiceSuggestions = [];
        _voiceStatus = "Choose a voice mode to help fill this report.";
    }
}

public sealed record ReportVoiceTurnLease(long Generation, CancellationToken CancellationToken);
