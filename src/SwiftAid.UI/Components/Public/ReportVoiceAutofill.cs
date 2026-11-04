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
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var clauses = statements.SelectMany(SplitClauses).ToArray();
        var evidenceClauses = SplitClauses(evidence);
        if (suggestion.Field == "age" && draft.SubjectScope == IntakeSubjectScope.Human &&
            string.IsNullOrWhiteSpace(draft.AgeYearsText) && !isModified(nameof(draft.AgeYearsText)))
        {
            const string pattern = @"^(?:the\s+patient(?:\s+patient)?|she|he)\s+(?:is\s+){1,2}(?<age>\d{1,3})\s+years?\s+old$";
            var matches = evidenceClauses.Select(item => (Clause: item, Match: Regex.Match(item, pattern, options)))
                .Where(item => item.Match.Success).ToArray();
            return matches.Length == 1 && clauses.Count(item => Regex.IsMatch(item, pattern, options)) == 1 &&
                IsSafeContext(matches[0].Clause, suggestion.Value, clauses) &&
                TryGetAge(suggestion.Value, out var age) &&
                int.TryParse(matches[0].Match.Groups["age"].Value, out var statedAge) && age == statedAge;
        }
        if (suggestion.Field == "locality" && string.IsNullOrWhiteSpace(draft.Locality) &&
            draft.LocalityPresence != PresenceState.Known && !isModified(nameof(draft.Locality)))
        {
            const string pattern = @"^(?:the\s+patient(?:\s+patient)?|she|he)\s+(?:lives\s+in\s+){1,2}(?<place>[\p{L}\p{N}][\p{L}\p{N} '\-]{0,199})$";
            var matches = evidenceClauses.Select(item => (Clause: item, Match: Regex.Match(item, pattern, options)))
                .Where(item => item.Match.Success).ToArray();
            return matches.Length == 1 && clauses.Count(item => Regex.IsMatch(item, pattern, options)) == 1 &&
                IsSafeContext(matches[0].Clause, suggestion.Value, clauses) &&
                string.Equals(matches[0].Match.Groups["place"].Value.Trim(), suggestion.Value.Trim(), StringComparison.Ordinal);
        }
        if (suggestion.Field == "country" && string.IsNullOrWhiteSpace(draft.CountryCode) &&
            !isModified(nameof(draft.CountryCode)))
        {
            const string pattern = @"^(?:this|it|the\s+incident)\s+(?:happened\s+){1,2}in\s+(?<country>[\p{L}\p{N}][\p{L}\p{N} '\-]{0,199})$";
            var matches = evidenceClauses.Select(item => (Clause: item, Match: Regex.Match(item, pattern, options)))
                .Where(item => item.Match.Success).ToArray();
            return matches.Length == 1 && clauses.Count(item => Regex.IsMatch(item, pattern, options)) == 1 &&
                IsSafeContext(matches[0].Clause, suggestion.Value, clauses) &&
                string.Equals(matches[0].Match.Groups["country"].Value.Trim(), suggestion.Value.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        return suggestion.Field == "observation" && IsSafeContext(evidence, suggestion.Value, statements) &&
            draft.GetObservations().Count == 0 && !isModified(nameof(draft.ObservationsText));
    }

    private static string[] SplitClauses(string statement) =>
        Regex.Split(statement,
                @"\s*,\s*|(?<=\s)(?=(?:(?:the\s+patient(?:\s+patient)?|she|he)\s+(?:(?:is\s+){1,2}\d{1,3}\s+years?\s+old\b|(?:lives\s+in\s+){1,2})|(?:this|it|the\s+incident)\s+(?:happened\s+){1,2}in\s+))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToArray();

    private static bool IsSafeContext(string selected, string value, IReadOnlyList<string> clauses)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        return !clauses.Any(item => !string.Equals(item, selected, StringComparison.Ordinal) &&
            (Regex.IsMatch(item, @"\b(?:actually|correction|instead|rather|wrong|maybe|perhaps|possibly|probably|uncertain|unknown|whether|if|think|guess|suspect|believe|might)\b", options) ||
             Regex.IsMatch(item, @"\b(?:do(?:es)?n't|do(?:es)?\s+not)\s+know\b|\bnot\s+sure\b", options) ||
             Regex.IsMatch(item, @"\b(?:not|never|no)$", options) ||
             (item.Contains(value, StringComparison.OrdinalIgnoreCase) &&
              Regex.IsMatch(item, @"\b(?:not|never|no|maybe|perhaps|uncertain|unknown|isn't|doesn't|don't)\b", options))));
    }

    public static bool TryGetAge(string value, out int age)
    {
        var match = Regex.Match(value.Trim(), @"^(?<age>\d{1,3})(?:\s+years?\s+old)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        age = 0;
        return match.Success && int.TryParse(match.Groups["age"].Value, out age);
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
