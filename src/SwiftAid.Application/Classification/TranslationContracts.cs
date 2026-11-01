using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwiftAid.Application.Classification;

public static partial class TranslationIdentity
{
    public const string Model = "facebook/nllb-200-distilled-1.3B";
    public const string Revision = "7be3e24664b38ce1cac29b8aeed6911aa0cf0576";
    public const string Configuration = "nllb-cpu-bfloat16-max512-greedy-v1";
    public static string? Language(string source) => source switch
    {
        "sn" or "sna" => "sna",
        "ny" or "nya" => "nya",
        "ts" or "tso" => "tso",
        "ss" or "ssw" => "ssw",
        _ => null
    };
    public static string Fingerprint(string organization, string workspace, string sensitivity, string text, string language) =>
        ClassificationFingerprint.Hash(JsonSerializer.Serialize(new[] { "translation-v1", organization, workspace,
            sensitivity, ClassificationFingerprint.Hash(ClassificationFingerprint.Normalize(text)), language, "eng",
            Model, Revision, Configuration, "nfc-whitespace-v1" }));

    /// <summary>Structural findings only. Never substitutes for a fluent review of meaning.</summary>
    public static IReadOnlyList<string> Assess(string source, string translation)
    {
        var findings = new List<string> { "human-language-review-required" };
        if (string.IsNullOrWhiteSpace(translation)) findings.Add("empty-translation");
        if (!Numbers().Matches(source).Select(x => x.Value).Order().SequenceEqual(
                Numbers().Matches(translation).Select(x => x.Value).Order())) findings.Add("numeric-content-changed");
        if (translation.Length > Math.Max(200, source.Length * 4) || translation.Length * 5 < source.Length)
            findings.Add("unusual-length-ratio");
        return findings;
    }

    [GeneratedRegex(@"\d+(?:[.,]\d+)*", RegexOptions.CultureInvariant)]
    private static partial Regex Numbers();
}

public sealed record TranslationProvenance(Guid ArtifactId, string Fingerprint, string SourceLanguage,
    string ModelRevision, string ReviewStatus, long ReviewVersion);
public sealed record TranslationReviewRequest(Guid DecisionId, long ExpectedVersion, bool Approve);
public sealed record TranslationEvidence(Guid ArtifactId, string State, string SourceLanguage, string TargetLanguage,
    string ModelId, string ModelRevision, string ReviewStatus, long ReviewVersion,
    IReadOnlyList<string> Findings, string? TranslatedText, string? FailureCode, DateTimeOffset CreatedAtUtc,
    string OriginalText, bool CanReview);
