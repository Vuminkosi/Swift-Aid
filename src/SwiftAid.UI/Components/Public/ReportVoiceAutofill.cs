using System.Text.RegularExpressions;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.UI.Components.Public;

/// <summary>Limits automatic edits to complete, unambiguous statements and untouched fields.</summary>
public static class ReportVoiceAutofill
{
    public static bool CanApply(ReportVoiceSuggestion suggestion, string transcript,
        PublicReportDraft draft, Func<string, bool> isModified)
    {
        var evidence = NormalizeStatement(suggestion.Evidence);
        if (string.IsNullOrWhiteSpace(evidence)) return false;
        var fragments = Regex.Split(transcript, @"(?<=[.!?])\s+|[\r\n]+")
            .Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
        var statements = fragments.Select(NormalizeStatement).ToArray();
        var matching = fragments.Where(item => string.Equals(NormalizeStatement(item), evidence, StringComparison.Ordinal)).ToArray();
        if (matching.Length != 1 || matching[0].TrimEnd().EndsWith('?')) return false;
        var statement = evidence;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (statements.Any(item => !string.Equals(item, evidence, StringComparison.Ordinal) &&
            (Regex.IsMatch(item, @"\b(?:actually|correction|instead|rather|wrong)\b", options) ||
             (item.Contains(suggestion.Value, StringComparison.OrdinalIgnoreCase) &&
              Regex.IsMatch(item, @"\b(?:not|never|no|maybe|perhaps|uncertain|unknown|isn't|doesn't|don't)\b", options)))))
            return false;
        if (suggestion.Field == "age" && draft.SubjectScope == IntakeSubjectScope.Human &&
            string.IsNullOrWhiteSpace(draft.AgeYearsText) && !isModified(nameof(draft.AgeYearsText)))
        {
            const string pattern = @"^(?:the\s+patient|she|he)\s+is\s+(?<age>\d{1,3})\s+years?\s+old$";
            var match = Regex.Match(statement, pattern, options);
            return statements.Count(item => Regex.IsMatch(item, pattern, options)) == 1 && match.Success &&
                string.Equals(match.Groups["age"].Value, suggestion.Value, StringComparison.Ordinal) &&
                int.TryParse(suggestion.Value, out _);
        }
        if (suggestion.Field == "locality" && string.IsNullOrWhiteSpace(draft.Locality) &&
            draft.LocalityPresence != PresenceState.Known && !isModified(nameof(draft.Locality)))
        {
            const string pattern = @"^(?:the\s+patient|she|he)\s+lives\s+in\s+(?<place>[\p{L}\p{N}][\p{L}\p{N} '\-]{0,199})$";
            var match = Regex.Match(statement, pattern, options);
            return statements.Count(item => Regex.IsMatch(item, pattern, options)) == 1 && match.Success &&
                string.Equals(match.Groups["place"].Value.Trim(), suggestion.Value.Trim(), StringComparison.Ordinal);
        }
        if (suggestion.Field == "country" && string.IsNullOrWhiteSpace(draft.CountryCode) &&
            !isModified(nameof(draft.CountryCode)))
        {
            const string pattern = @"^(?:this|it|the\s+incident)\s+happened\s+in\s+(?<country>[\p{L}\p{N}][\p{L}\p{N} '\-]{0,199})$";
            var match = Regex.Match(statement, pattern, options);
            return statements.Count(item => Regex.IsMatch(item, pattern, options)) == 1 && match.Success &&
                string.Equals(match.Groups["country"].Value.Trim(), suggestion.Value.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        return suggestion.Field == "observation" && draft.GetObservations().Count == 0 &&
            !isModified(nameof(draft.ObservationsText));
    }

    public static bool TryGetCountryCode(ReportVoiceSuggestion suggestion, string transcript,
        PublicReportDraft draft, Func<string, bool> isModified, IReadOnlyList<PublicCountryOption> countries,
        out string? countryCode)
    {
        countryCode = null;
        if (suggestion.Field != "country" || !CanApply(suggestion, transcript, draft, isModified)) return false;
        var matches = countries.Where(country => string.Equals(country.Name, suggestion.Value.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return false;
        countryCode = matches[0].Code;
        return true;
    }

    private static string NormalizeStatement(string value) => value.Trim().TrimEnd('.', '!', '?').TrimEnd();
}
