using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwiftAid.Contracts.Events;

namespace SwiftAid.Application.Classification;

public sealed record ClassificationLabel(string Id, string Category, string Description);

/// <summary>All inference-affecting settings form one immutable, content-addressed configuration.</summary>
public sealed record ClassificationProfile(
    string ModelId, string ModelRevision, string TaxonomyVersion, string PreprocessingVersion,
    string HypothesisVersion, string HypothesisTemplate, string SourceLanguage,
    int MaximumTokens, double SelectionThreshold, IReadOnlyList<ClassificationLabel> Labels)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TranslationProvenance? Translation { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Hash => ClassificationFingerprint.Hash(JsonSerializer.Serialize(this));

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelId) || !Regex.IsMatch(ModelRevision, "^[a-f0-9]{40}$") ||
            string.IsNullOrWhiteSpace(TaxonomyVersion) || PreprocessingVersion != "nfc-whitespace-v1" ||
            string.IsNullOrWhiteSpace(HypothesisVersion) || HypothesisTemplate.Split("{}").Length != 2 ||
            !Regex.IsMatch(SourceLanguage, "^[a-z]{2,3}$") || MaximumTokens is < 64 or > 1024 ||
            !double.IsFinite(SelectionThreshold) || SelectionThreshold is < 0 or > 1 ||
            Labels.Count is < 1 or > 32 || Labels.Select(label => label.Id).Distinct().Count() != Labels.Count ||
            Labels.Any(label => !Regex.IsMatch(label.Id, "^[a-z][a-z0-9-]{0,63}$") ||
                string.IsNullOrWhiteSpace(label.Category) || string.IsNullOrWhiteSpace(label.Description) || label.Description.Length > 200))
            throw new ArgumentException("Invalid classification profile.");
    }
}

public static partial class ClassificationFingerprint
{
    public static string Normalize(string value) => Whitespace().Replace(value.Normalize(NormalizationForm.FormC), " ").Trim();
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Create(string organization, string workspace, string sensitivity, string content, ClassificationProfile profile) =>
        Hash(JsonSerializer.Serialize(new[] { "classification-v1", organization, workspace, sensitivity, Hash(content), profile.Hash }));

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

public sealed record ClassificationClaimRequest(IngestionNotice Notice, Guid? BackfillJobId = null);
public sealed record ClassificationLease(Guid ResultId, Guid LeaseId, string ConfigurationHash, string Fingerprint,
    DateTimeOffset LeaseUntilUtc, string Text, ClassificationProfile Profile);
public sealed record ClassificationClaim(string State, ClassificationLease? Lease = null, string? FailureCode = null, string? ConfigurationHash = null);
public sealed record ClassificationPrediction(string LabelId, double Score);
public sealed record ClassificationCompletion(Guid ResultId, Guid LeaseId, string ConfigurationHash, string Fingerprint,
    IReadOnlyList<ClassificationPrediction> Predictions, string? AbstentionReason, int InputTokens, double InferenceMilliseconds);
public sealed record ClassificationFailure(Guid ResultId, Guid LeaseId, string Code);
public sealed record ClassificationRenewal(Guid ResultId, Guid LeaseId);
public sealed record ClassificationQuarantine(ulong StreamSequence, string PayloadSha256, string Reason);

public static class ClassificationResultValidation
{
    public static void Validate(ClassificationCompletion completion, ClassificationProfile profile)
    {
        if (completion.InputTokens < 0 || completion.InputTokens > profile.MaximumTokens ||
            !double.IsFinite(completion.InferenceMilliseconds) || completion.InferenceMilliseconds < 0 ||
            completion.Predictions.Count > 32 ||
            completion.Predictions.Any(p => !double.IsFinite(p.Score) || p.Score is < 0 or > 1) ||
            completion.Predictions.Select(p => p.LabelId).Distinct().Count() != completion.Predictions.Count)
            throw new ArgumentException("Invalid classification result.");
        if (completion.AbstentionReason is not null)
        {
            if (completion.AbstentionReason is not ("unsupported-language" or "input-too-long" or "empty-input") || completion.Predictions.Count != 0)
                throw new ArgumentException("Invalid abstention.");
        }
        else if (profile.SourceLanguage != "en" || !completion.Predictions.Select(p => p.LabelId).Order().SequenceEqual(profile.Labels.Select(l => l.Id).Order()))
            throw new ArgumentException("Incomplete or unsupported predictions.");
    }
}
