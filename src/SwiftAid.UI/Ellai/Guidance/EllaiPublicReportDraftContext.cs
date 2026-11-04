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

    public void Register(object owner, Func<int, bool> setAge, Func<string, bool> addObservation)
    {
        lock (_gate) { _owner = owner; _setAge = setAge; _addObservation = addObservation; }
    }

    public void Remove(object owner)
    {
        lock (_gate)
            if (ReferenceEquals(_owner, owner)) { _owner = null; _setAge = null; _addObservation = null; }
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
        if (!Regex.IsMatch(utterance, @"^\s*(?:she|he|the patient)\s+(?:has|reports|is experiencing|experienced)\s+\S.*[.!?]?\s*$", options))
            return false;
        Func<string, bool>? addObservation;
        lock (_gate) addObservation = _addObservation;
        return addObservation?.Invoke(utterance.Trim()) == true;
    }
}
