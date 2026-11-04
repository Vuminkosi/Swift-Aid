using System.Text.RegularExpressions;

namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>Short lived bridge from a validated Ellai intent to the mounted public report form.</summary>
public sealed class EllaiPublicReportDraftContext
{
    private const int MaximumAgeCorrectionHistory = 10;

    private readonly object _gate = new();
    private object? _owner;
    private Func<int?, bool>? _setAge;
    private Func<int?>? _getAge;
    private Func<string, bool>? _addObservation;
    private Func<string?, bool>? _setLocality;
    private Func<string, bool>? _appendNarrative;
    private readonly List<PendingEdit> _pending = [];
    private DateTimeOffset _captureExpiresAt;
    private bool _captureActive;

    private readonly List<int> _previousAges = [];
    private enum PendingEditKind { Age, PreviousAge, Observation, Locality, Narrative }
    private sealed record PendingEdit(PendingEditKind Kind, int? Age = null, string? Text = null);

    public void BeginReportCapture()
    {
        lock (_gate)
        {
            _pending.Clear();
            _previousAges.Clear();
            _captureActive = true;
            _captureExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        }
    }

    public void Register(object owner, Func<int?, bool> setAge, Func<int?> getAge, Func<string, bool> addObservation,
        Func<string?, bool> setLocality, Func<string, bool> appendNarrative)
    {
        lock (_gate)
        {
            _owner = owner;
            _setAge = setAge;
            _getAge = getAge;
            _addObservation = addObservation;
            _setLocality = setLocality;
            _appendNarrative = appendNarrative;
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
        }

        var succeeded = true;
        foreach (var edit in pending) succeeded &= Apply(edit);
        return succeeded;
    }

    public void Remove(object owner)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner)) return;
            _owner = null;
            _setAge = null;
            _getAge = null;
            _addObservation = null;
            _setLocality = null;
            _appendNarrative = null;
            _pending.Clear();
            _previousAges.Clear();
            _captureActive = false;
        }
    }

    public bool ApplyAgeCorrection(string? utterance)
    {
        if (utterance is null) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (Regex.IsMatch(utterance, @"^\s*I\s+(?:do\s+not|don't)\s+know\s+(?:the\s+)?(?:patient's\s+)?age[.!?]?\s*$", options) ||
            Regex.IsMatch(utterance, @"^\s*age\s+(?:is\s+)?unknown[.!?]?\s*$", options))
            return Apply(new(PendingEditKind.Age));
        if (Regex.IsMatch(utterance,
            @"^\s*(?:wait,?\s+)?(?:the\s+)?age\s+(?:was\s+)?(?:correct|right)\s+the\s+first\s+time[.!?]?\s*$", options))
            return Apply(new(PendingEditKind.PreviousAge));
        var match = Regex.Match(utterance,
            @"^\s*(?:change|correct|update|set)\s+(?:(?:her|his|the patient's|patient's)\s+)?age\s+(?:to|is)\s+(\d{1,10})\s*[.!?]?\s*$", options);
        if (!match.Success)
            match = Regex.Match(utterance, @"^\s*(?:(?:the\s+)?patient|she|he)\s+(?:is\s+)?(?:actually\s+)?(\d{1,10})(?:\s+years?\s+old)?\s*[.!?]?\s*$", options);
        if (!match.Success)
            match = Regex.Match(utterance,
                @"^\s*(?:(?:create|start|prepare)\s+(?:a\s+)?(?:public\s+)?(?:case\s+)?report\s+for\s+(?:a\s+)?|)(\d{1,10})[- ]year[- ]old\s+(?:patient|woman|man|girl|boy)\s*[.!?]?\s*$", options);
        return match.Success && int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var age) && Apply(new(PendingEditKind.Age, Age: age));
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
        if (Regex.IsMatch(utterance, @"^\s*I\s+(?:do\s+not|don't)\s+know\s+(?:where\s+(?:the\s+)?patient\s+lives|the\s+(?:patient's\s+)?(?:locality|location))[.!?]?\s*$", options))
            return Apply(new(PendingEditKind.Locality));
        var match = Regex.Match(utterance,
            @"^\s*(?:(?:she|he|the patient)\s+)?lives?\s+in\s+([\p{L}\p{N}][\p{L}\p{N} '\-]{0,199})[.!?]?\s*$", options);
        if (!match.Success)
            match = Regex.Match(utterance,
                @"^\s*(?:(?:she|he|the patient)\s+)?(?:is\s+)?from\s+([\p{L}\p{N}][\p{L}\p{N} '\-]{0,199})[.!?]?\s*$", options);
        return match.Success && Apply(new(PendingEditKind.Locality, Text: match.Groups[1].Value.Trim()));
    }

    public bool ApplyReportNarrativeStatement(string? utterance)
    {
        if (utterance is null || utterance.Length > 500 || utterance.Contains('\r') || utterance.Contains('\n')) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var isConcern = Regex.IsMatch(utterance,
            @"^\s*I\s+(?:want|would\s+like)\s+to\s+report\s+(?:a|an)\s+(?:suspected\s+)?[^.!?\r\n]+\s+(?:case|patient|animal|concern)[.!?]?\s*$", options);
        var isOnset = Regex.IsMatch(utterance,
            @"^\s*It\s+(?:started|began)\s+(?:yesterday|today|on\s+[^.!?\r\n]+)[.!?]?\s*$", options);
        var isReportedSex = Regex.IsMatch(utterance,
            @"^\s*(?:the\s+)?patient\s+is\s+(?:female|male)[.!?]?\s*$", options);
        return (isConcern || isOnset || isReportedSex) && Apply(new(PendingEditKind.Narrative, Text: utterance.Trim()));
    }

    private bool Apply(PendingEdit edit)
    {
        Func<bool>? apply;
        lock (_gate)
        {
            apply = edit.Kind switch
            {
                PendingEditKind.Age when _setAge is not null => () => ApplyAgeWithHistory(edit.Age),
                PendingEditKind.PreviousAge when _setAge is not null => RestorePreviousAge,
                PendingEditKind.Observation when _addObservation is { } addObservation => () => addObservation(edit.Text!),
                PendingEditKind.Locality when _setLocality is { } setLocality => () => setLocality(edit.Text),
                PendingEditKind.Narrative when _appendNarrative is { } appendNarrative => () => appendNarrative(edit.Text!),
                _ => null
            };
            if (apply is null)
            {
                if (!_captureActive || _pending.Count >= 10) return false;
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

    private bool ApplyAgeWithHistory(int? age)
    {
        Func<int?, bool>? setAge;
        Func<int?>? getAge;
        lock (_gate)
        {
            setAge = _setAge;
            getAge = _getAge;
        }
        var previous = getAge?.Invoke();
        if (setAge?.Invoke(age) != true) return false;
        if (previous is { } value && value != age)
        {
            lock (_gate)
            {
                if (_previousAges.Count == MaximumAgeCorrectionHistory)
                    _previousAges.RemoveAt(0);
                _previousAges.Add(value);
            }
        }
        return true;
    }

    private bool RestorePreviousAge()
    {
        Func<int?, bool>? setAge;
        int previous;
        lock (_gate)
        {
            if (_previousAges.Count == 0 || _setAge is not { } callback) return false;
            previous = _previousAges[^1];
            setAge = callback;
        }
        if (setAge(previous) != true) return false;
        lock (_gate)
        {
            if (_previousAges.Count > 0 && _previousAges[^1] == previous)
                _previousAges.RemoveAt(_previousAges.Count - 1);
        }
        return true;
    }
}
