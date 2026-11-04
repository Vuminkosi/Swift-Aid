using System.Text.RegularExpressions;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>Ephemeral bridge to one currently mounted public report draft. Proposal payloads carry no values.</summary>
public sealed class EllaiPublicReportDraftContext
{
    private readonly object _gate = new();
    private object? _owner;
    private Func<int, bool>? _setAge;
    private Func<string, bool>? _addObservation;
    private Func<string, bool>? _setLocality;
    private Func<string, bool>? _appendNarrative;

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
        Func<int, bool>? setAge;
        lock (_gate) setAge = _setAge;
        return setAge?.Invoke(age) == true;
    }

    public bool ApplyReportedObservation(string? utterance)
    {
        if (utterance is null || utterance.Length > 1000 || utterance.Contains('\r') || utterance.Contains('\n')) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (!Regex.IsMatch(utterance, @"^\s*(?:she|he|the patient)\s+(?:has|reports|is experiencing|experienced)\s+[^.!?\r\n]+[.!?]?\s*$", options))
            return false;
        Func<string, bool>? addObservation;
        lock (_gate) addObservation = _addObservation;
        return addObservation?.Invoke(utterance.Trim()) == true;
    }

    public bool ApplyReportedLocality(string? utterance)
    {
        if (utterance is null || utterance.Length > 240 || utterance.Contains('\r') || utterance.Contains('\n')) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var match = Regex.Match(utterance,
            @"^\s*(?:(?:she|he|the patient)\s+)?lives?\s+in\s+([\p{L}\p{N}][\p{L}\p{N} '\-]{0,199})[.!?]?\s*$", options);
        if (!match.Success) return false;
        Func<string, bool>? setLocality;
        lock (_gate) setLocality = _setLocality;
        return setLocality?.Invoke(match.Groups[1].Value.Trim()) == true;
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
        Func<string, bool>? appendNarrative;
        lock (_gate) appendNarrative = _appendNarrative;
        return appendNarrative?.Invoke(utterance.Trim()) == true;
    }
}
