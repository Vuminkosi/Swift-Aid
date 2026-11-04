using System.Text.RegularExpressions;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.UI.Ellai.Guidance;

public enum EllaiRelativeOnsetDay { Today, Yesterday }

/// <summary>Ephemeral bridge to the active report flow. Proposal payloads carry no report values.</summary>
public sealed partial class EllaiPublicReportDraftContext
{
    private readonly object _gate = new();
    private object? _owner;
    private Func<int, bool>? _setAge;
    private Func<string, bool>? _addObservation;
    private Func<string, bool>? _removeObservation;
    private Func<string, bool>? _setLocality;
    private Func<DateOnly?, EllaiRelativeOnsetDay?, string, bool>? _setOnsetDate;
    private Func<string, bool>? _appendNarrative;
    private Func<ReportedGender, bool>? _setGender;
    private Func<bool>? _revertAge;
    private readonly List<PendingEdit> _pending = [];
    private DateTimeOffset _captureExpiresAt;
    private bool _captureActive;

    private enum PendingEditKind { Age, AgeRevert, Gender, Observation, RemoveObservation, Locality, Onset, Narrative }
    private sealed record PendingEdit(PendingEditKind Kind, int? Age = null, ReportedGender? Gender = null,
        string? Text = null, DateOnly? Date = null, EllaiRelativeOnsetDay? RelativeOnsetDay = null);

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
        Func<string, bool>? setLocality = null, Func<string, bool>? appendNarrative = null,
        Func<ReportedGender, bool>? setGender = null, Func<bool>? revertAge = null,
        Func<string, bool>? removeObservation = null,
        Func<DateOnly?, EllaiRelativeOnsetDay?, string, bool>? setOnsetDate = null)
    {
        lock (_gate)
        {
            _owner = owner;
            _setAge = setAge;
            _addObservation = addObservation;
            _removeObservation = removeObservation ?? (_ => false);
            _setLocality = setLocality ?? (_ => false);
            _setOnsetDate = setOnsetDate ?? ((_, _, _) => false);
            _appendNarrative = appendNarrative ?? (_ => false);
            _setGender = setGender ?? (_ => false);
            _revertAge = revertAge ?? (() => false);
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
                _removeObservation = null;
                _setLocality = null;
                _setOnsetDate = null;
                _appendNarrative = null;
                _setGender = null;
                _revertAge = null;
                _pending.Clear();
                _captureActive = false;
            }
    }

    public bool ApplyAgeCorrection(string? utterance)
    {
        if (utterance is null || utterance.Length > 200 || utterance.Contains('\r') || utterance.Contains('\n')) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (Regex.IsMatch(utterance, @"^\s*(?:wait[, ]+)?(?:the\s+)?age\s+was\s+(?:correct|right)\s+the\s+first\s+time[.!?]?\s*$", options))
            return Apply(new(PendingEditKind.AgeRevert));

        var match = Regex.Match(utterance, @"^\s*(?:change|correct|update|set)\s+(?:(?:her|his|the patient's|patient's)\s+)?age\s+(?:to|is)\s+(\d{1,10})\s*[.!?]?\s*$", options);
        if (!match.Success)
            match = Regex.Match(utterance, @"^\s*(?:(?:the\s+)?patient|she|he)\s+(?:is\s+)?(?:actually\s+)?(\d{1,10})(?:\s+years?\s+old)?\s*[.!?]?\s*$", options);
        if (!match.Success)
            match = Regex.Match(utterance, @"^\s*(?:(?:create|make|start|open)\s+(?:a\s+)?(?:report|draft)\s+for\s+(?:(?:a|the)\s+)?)?(\d{1,10})[- ]year[- ]old\s+(patient|woman|man|girl|boy)\s*[.!?]?\s*$", options);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var age)) return false;
        var applied = Apply(new(PendingEditKind.Age, Age: age));
        var reportedGender = match.Groups.Count > 2 ? GenderForDescriptor(match.Groups[2].Value) : null;
        return applied && (reportedGender is null || Apply(new(PendingEditKind.Gender, Gender: reportedGender)));
    }

    public bool ApplyInitialReportDetails(string? utterance)
    {
        if (utterance is null || utterance.Length > 500 || utterance.Contains('\r') || utterance.Contains('\n')) return false;
        var match = Regex.Match(utterance,
            @"^\s*(?:create|make|start)\s+(?:a\s+)?(?:draft\s+)?(?:case|report)\s+for\s+(?:a\s+)?(?<age>\d{1,3})[- ]year[- ]old\s+(?<subject>patient|woman|man|girl|boy)(?:\s+in\s+(?<locality>[\p{L}\p{N}][\p{L}\p{N} '\-]{0,199}?))?(?:\s+with\s+(?<observation>[\p{L}\p{N}][\p{L}\p{N} '\-]{0,199}))?[.!?]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["age"].Value,
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var age))
            return false;

        var applied = Apply(new(PendingEditKind.Age, Age: age));
        var reportedGender = GenderForDescriptor(match.Groups["subject"].Value);
        if (reportedGender is { } gender) applied &= Apply(new(PendingEditKind.Gender, Gender: gender));
        if (match.Groups["locality"].Success)
            applied &= Apply(new(PendingEditKind.Locality, Text: match.Groups["locality"].Value.Trim()));
        if (match.Groups["observation"].Success)
            applied &= Apply(new(PendingEditKind.Observation, Text: match.Groups["observation"].Value.Trim()));
        return applied;
    }

    public bool ApplyReportedGender(string? utterance)
    {
        if (utterance is null || utterance.Length > 200 || utterance.Contains('\r') || utterance.Contains('\n')) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var match = Regex.Match(utterance,
            @"^\s*(?:(?:the\s+)?patient|she|he)\s+(?:is|was)\s+(female|male|other)\s*[.!?]?\s*$", options);
        if (!match.Success) return false;
        var gender = match.Groups[1].Value.ToLowerInvariant() switch
        {
            "female" => ReportedGender.Female,
            "male" => ReportedGender.Male,
            "other" => ReportedGender.Other,
            _ => (ReportedGender?)null
        };
        return gender is { } value && Apply(new(PendingEditKind.Gender, Gender: value));
    }

    private static ReportedGender? GenderForDescriptor(string descriptor) => descriptor.ToLowerInvariant() switch
    {
        "woman" or "girl" => ReportedGender.Female,
        "man" or "boy" => ReportedGender.Male,
        _ => null
    };

    public bool ApplyReportedObservation(string? utterance)
    {
        if (utterance is null || utterance.Length > 1000 || utterance.Contains('\r') || utterance.Contains('\n')) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (Regex.IsMatch(utterance, @"^\s*(?:she|he|the patient)\s+(?:has|reports|is experiencing|experienced)\s+[^.!?\r\n]+[.!?]?\s*$", options))
            return Apply(new(PendingEditKind.Observation, Text: utterance.Trim()));

        var addMatch = Regex.Match(utterance,
            @"^\s*(?:add|include)\s+(?<observation>[\p{L}\p{N}][\p{L}\p{N} '\-]{0,199}?)(?:\s+to\s+(?:the\s+)?(?:report|draft))?[.!?]?\s*$",
            options);
        if (!addMatch.Success) return false;
        var observation = addMatch.Groups["observation"].Value.Trim();
        return observation.Length is > 0 and <= 200 && Apply(new(PendingEditKind.Observation, Text: observation));
    }

    public static bool IsExplicitObservationRemoval(string? utterance) =>
        TryGetObservationRemoval(utterance, out _);

    public bool RemoveReportedObservation(string? utterance) =>
        TryGetObservationRemoval(utterance, out var observation) &&
        Apply(new(PendingEditKind.RemoveObservation, Text: observation));

    private static bool TryGetObservationRemoval(string? utterance, out string observation)
    {
        observation = string.Empty;
        if (utterance is null || utterance.Length > 300 || utterance.Contains('\r') || utterance.Contains('\n')) return false;
        var match = Regex.Match(utterance,
            @"^\s*(?:remove|delete)\s+(?:the\s+)?(?<observation>[\p{L}\p{N}][\p{L}\p{N} '\-]{0,199}?)(?:\.\s+It was recorded incorrectly\.?)?[.!?]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        observation = match.Groups["observation"].Value.Trim();
        return observation.Length is > 0 and <= 200;
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

    public bool ApplyReportedOnsetDate(string? utterance)
    {
        if (utterance is null || utterance.Length > 240 || utterance.Contains('\r') || utterance.Contains('\n')) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var match = Regex.Match(utterance,
            @"^\s*(?:symptoms?|signs?)\s+(?:started|began)\s+on\s+(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})[.!?]?\s*$", options);
        if (!match.Success)
            match = Regex.Match(utterance,
                @"^\s*(?:the\s+)?(?:symptom|sign)\s+onset\s+(?:date\s+)?(?:is|was)\s+(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})[.!?]?\s*$", options);
        if (match.Success && DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            return Apply(new(PendingEditKind.Onset, Text: utterance.Trim(), Date: date));

        match = Regex.Match(utterance,
            @"^\s*(?:it|(?:the\s+)?symptoms?|(?:the\s+)?signs?)\s+(?:started|began)\s+(?<relative>today|yesterday)[.!?]?\s*$", options);
        if (!match.Success) return false;
        var relativeDay = match.Groups["relative"].Value.Equals("today", StringComparison.OrdinalIgnoreCase)
            ? EllaiRelativeOnsetDay.Today : EllaiRelativeOnsetDay.Yesterday;
        return Apply(new(PendingEditKind.Onset, Text: utterance.Trim(), RelativeOnsetDay: relativeDay));
    }

    private bool Apply(PendingEdit edit)
    {
        Func<bool>? apply;
        lock (_gate)
        {
            apply = edit.Kind switch
            {
                PendingEditKind.Age when _setAge is { } setAge => () => setAge(edit.Age!.Value),
                PendingEditKind.AgeRevert when _revertAge is { } revertAge => revertAge,
                PendingEditKind.Gender when _setGender is { } setGender => () => setGender(edit.Gender!.Value),
                PendingEditKind.Observation when _addObservation is { } addObservation => () => addObservation(edit.Text!),
                PendingEditKind.RemoveObservation when _removeObservation is { } removeObservation => () => removeObservation(edit.Text!),
                PendingEditKind.Locality when _setLocality is { } setLocality => () => setLocality(edit.Text!),
                PendingEditKind.Onset when _setOnsetDate is { } setOnsetDate => () =>
                    setOnsetDate(edit.Date, edit.RelativeOnsetDay, edit.Text!),
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
