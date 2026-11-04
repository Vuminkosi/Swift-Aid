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
        var statement = transcript.Trim().TrimEnd('.', '!', '?').TrimEnd();
        var evidence = suggestion.Evidence.Trim().TrimEnd('.', '!', '?').TrimEnd();
        if (string.IsNullOrWhiteSpace(statement) || !string.Equals(statement, evidence, StringComparison.Ordinal)) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (suggestion.Field == "age" && draft.SubjectScope == IntakeSubjectScope.Human &&
            string.IsNullOrWhiteSpace(draft.AgeYearsText) && !isModified(nameof(draft.AgeYearsText)))
        {
            var match = Regex.Match(statement, @"^(?:the\s+patient|she|he)\s+is\s+(?<age>\d{1,3})\s+years?\s+old$", options);
            return match.Success && string.Equals(match.Groups["age"].Value, suggestion.Value, StringComparison.Ordinal) &&
                int.TryParse(suggestion.Value, out _);
        }
        if (suggestion.Field == "locality" && string.IsNullOrWhiteSpace(draft.Locality) &&
            draft.LocalityPresence != PresenceState.Known && !isModified(nameof(draft.Locality)))
        {
            var match = Regex.Match(statement,
                @"^(?:the\s+patient|she|he)\s+lives\s+in\s+(?<place>[\p{L}\p{N}][\p{L}\p{N} '\-]{0,199})$", options);
            return match.Success && string.Equals(match.Groups["place"].Value.Trim(), suggestion.Value.Trim(), StringComparison.Ordinal);
        }
        return suggestion.Field == "observation" && draft.GetObservations().Count == 0 &&
            !isModified(nameof(draft.ObservationsText));
    }
}
