using System.Text.RegularExpressions;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>Ephemeral bridge to the active report flow. Proposal payloads carry no report values.</summary>
public sealed class EllaiPublicReportDraftContext
{
    private readonly object _gate = new();
    private object? _owner;
    private Func<int, bool>? _setAge;
    private Func<string, bool>? _addObservation;
    private Func<string, bool>? _setLocality;
    private Func<string, bool>? _appendNarrative;
    private readonly List<PendingEdit> _pending = [];
    private DateTimeOffset _captureExpiresAt;
    private bool _captureActive;

    private enum PendingEditKind { Age, Observation, Locality, Narrative }
    private sealed record PendingEdit(PendingEditKind Kind, int? Age = null, string? Text = null);

    public void BeginReportCapture()
    {
        lock (_gate)
        {
            if (!_captureActive || DateTimeOffset.UtcNow >= _captureExpiresAt) _pending.Clear();
            _captureActive = true;
            _captureExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        }
    }

    public void Register(object owner, Func<int, bool> setAge, Func<string, bool> addObservation,
        Func<string, bool>? setLocality = null, Func<string, bool>? appendNarrative = null)
    {
        lock (_gate)
        {
            _owner = owner;
            _setAge = setAge;
            _addObservation = addObservation;
            _setLocality = setLocality ?? (_ => false);
            _appendNarrative = appendNarrative ?? (_ => false);
        }
    }

    public bool FlushPending(object owner)
    {
        PendingEdit[] pending;
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner)) return false;
            if (!_captureActive || DateTimeOffset.UtcNow >= _captureExpiresAt)
            {
                var hadPendingEdits = _pending.Count > 0;
                _pending.Clear();
                _captureActive = false;
                return !hadPendingEdits;
            }
            pending = _pending.ToArray();
            _pending.Clear();
            _captureActive = false;
        }

        var succeeded = true;
        foreach (var edit in pending) succeeded &= Apply(edit);
        return succeeded;
    }

    public void Remove(object owner)
    {
        lock (_gate)
            if (ReferenceEquals(_owner, owner))
            {
                _owner = null;
                _setAge = null;
                _addObservation = null;
                _setLocality = null;
                _appendNarrative = null;
                _pending.Clear();
                _captureActive = false;
            }
    }

    public bool ApplyAgeCorrection(string? utterance)
    {
        if (utterance is null) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var match = Regex.Match(utterance, @"^\s*(?:change|correct|update|set)\s+(?:(?:her|his|the patient's|patient's)\s+)?age\s+(?:to|is)\s+(\d{1,10})\s*[.!?]?\s*$", options);
        if (!match.Success)
            match = Regex.Match(utterance, @"^\s*(?:(?:the\s+)?patient|she|he)\s+(?:is\s+)?(\d{1,10})\s+years?\s+old\s*[.!?]?\s*$", options);
        if (!match.Success)
            match = Regex.Match(utterance, @"^\s*(\d{1,10})[- ]year[- ]old\s+(?:patient|woman|man|girl|boy)\s*[.!?]?\s*$", options);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var age)) return false;
        return Apply(new(PendingEditKind.Age, Age: age));
    }

    public bool ApplyReportedObservation(string? utterance)
    {
        if (utterance is null || utterance.Length > 1000 || utterance.Contains('\r') || utterance.Contains('\n')) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (!Regex.IsMatch(utterance, @"^\s*(?:she|he|the patient)\s+(?:has|reports|is experiencing|experienced)\s+[^.!?\r\n]+[.!?]?\s*$", options))
            return false;
        return Apply(new(PendingEditKind.Observation, Text: utterance.Trim()));
    }

    public bool ApplyReportedLocality(string? utterance)
    {
        if (utterance is null || utterance.Length > 240 || utterance.Contains('\r') || utterance.Contains('\n')) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var match = Regex.Match(utterance,
            @"^\s*(?:(?:she|he|the patient)\s+)?lives?\s+in\s+([\p{L}\p{N}][\p{L}\p{N} '\-]{0,199})[.!?]?\s*$", options);
        if (!match.Success) return false;
        return Apply(new(PendingEditKind.Locality, Text: match.Groups[1].Value.Trim()));
    }

    public bool ApplyReportNarrativeStatement(string? utterance)
    {
        if (utterance is null || utterance.Length > 500 || utterance.Contains('\r') || utterance.Contains('\n')) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var isConcern = Regex.IsMatch(utterance,
            @"^\s*I\s+(?:want|would\s+like)\s+to\s+report\s+a\s+suspected\s+[^.!?\r\n]+\s+case[.!?]?\s*$", options);
        var isOnset = Regex.IsMatch(utterance,
            @"^\s*It\s+(?:started|began)\s+(?:yesterday|today|on\s+[^.!?\r\n]+)[.!?]?\s*$", options);
        if (!isConcern && !isOnset) return false;
        return Apply(new(PendingEditKind.Narrative, Text: utterance.Trim()));
    }

    private bool Apply(PendingEdit edit)
    {
        Func<bool>? apply;
        lock (_gate)
        {
            apply = edit.Kind switch
            {
                PendingEditKind.Age when _setAge is { } setAge => () => setAge(edit.Age!.Value),
                PendingEditKind.Observation when _addObservation is { } addObservation => () => addObservation(edit.Text!),
                PendingEditKind.Locality when _setLocality is { } setLocality => () => setLocality(edit.Text!),
                PendingEditKind.Narrative when _appendNarrative is { } appendNarrative => () => appendNarrative(edit.Text!),
                _ => null
            };
            if (apply is null)
            {
                if (!_captureActive || _pending.Count >= 10)
                    return false;
                if (DateTimeOffset.UtcNow >= _captureExpiresAt)
                {
                    _captureActive = false;
                    _pending.Clear();
                    return false;
                }

                _pending.Add(edit);
                return true;
            }
        }

        return apply();
    }
}
