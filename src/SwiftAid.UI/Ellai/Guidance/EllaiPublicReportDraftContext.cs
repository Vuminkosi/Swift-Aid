using System.Text.RegularExpressions;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>Ephemeral bridge to one currently mounted public report draft. Proposal payloads carry no values.</summary>
public sealed class EllaiPublicReportDraftContext
{
    private readonly object _gate = new();
    private object? _owner;
    private Func<int, bool>? _setAge;

    public void Register(object owner, Func<int, bool> setAge)
    {
        lock (_gate) { _owner = owner; _setAge = setAge; }
    }

    public void Remove(object owner)
    {
        lock (_gate) if (ReferenceEquals(_owner, owner)) { _owner = null; _setAge = null; }
    }

    public bool ApplyAgeCorrection(string? utterance)
    {
        if (utterance is null) return false;
        var match = Regex.Match(utterance, @"^\s*(?:change|correct|update|set)\s+(?:(?:her|his|the patient's|patient's)\s+)?age\s+(?:to|is)\s+(\d{1,10})\s*[.!?]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var age)) return false;
        Func<int, bool>? setAge;
        lock (_gate) setAge = _setAge;
        return setAge?.Invoke(age) == true;
    }
}
