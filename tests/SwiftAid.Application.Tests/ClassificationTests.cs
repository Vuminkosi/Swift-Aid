using SwiftAid.Application.Classification;

namespace SwiftAid.Application.Tests;

public sealed class ClassificationTests
{
    private static ClassificationProfile Profile => new("facebook/bart-large-mnli", new string('a', 40), "taxonomy-v1",
        "nfc-whitespace-v1", "template-v1", "This report describes {}.", "en", 512, 0.7,
        [new("respiratory", "presentation", "cough or breathing symptoms")]);

    [Fact]
    public void Fingerprint_separates_scope_and_all_inference_versions()
    {
        var profile = Profile;
        profile.Validate();
        var initial = ClassificationFingerprint.Create("org", "workspace", "standard", "fever", profile);
        Assert.NotEqual(initial, ClassificationFingerprint.Create("other", "workspace", "standard", "fever", profile));
        Assert.NotEqual(initial, ClassificationFingerprint.Create("org", "other", "standard", "fever", profile));
        Assert.NotEqual(initial, ClassificationFingerprint.Create("org", "workspace", "restricted", "fever", profile));
        foreach (var changed in new[] { profile with { ModelRevision = new string('b', 40) }, profile with { TaxonomyVersion = "v2" },
            profile with { HypothesisVersion = "v2" }, profile with { SourceLanguage = "sn" }, profile with { MaximumTokens = 256 },
            profile with { SelectionThreshold = 0.8 }, profile with { Labels = [new("respiratory", "presentation", "different definition")] } })
            Assert.NotEqual(initial, ClassificationFingerprint.Create("org", "workspace", "standard", "fever", changed));
    }

    [Fact]
    public void Normalization_preserves_negation_and_unicode_equivalence()
    {
        Assert.Equal("No fever. Café", ClassificationFingerprint.Normalize("  No\tfever. Cafe\u0301\r\n"));
        Assert.NotEqual(ClassificationFingerprint.Normalize("no fever"), ClassificationFingerprint.Normalize("fever"));
    }

    [Fact]
    public void Result_rejects_missing_labels_nan_and_unsupported_language()
    {
        var request = new ClassificationCompletion(Guid.NewGuid(), Guid.NewGuid(), Profile.Hash, "fingerprint",
            [new("respiratory", 0.8)], null, 12, 10);
        ClassificationResultValidation.Validate(request, Profile);
        Assert.Throws<ArgumentException>(() => ClassificationResultValidation.Validate(request with { Predictions = [] }, Profile));
        Assert.Throws<ArgumentException>(() => ClassificationResultValidation.Validate(request with { Predictions = [new("respiratory", double.NaN)] }, Profile));
        Assert.Throws<ArgumentException>(() => ClassificationResultValidation.Validate(request, Profile with { SourceLanguage = "sn" }));
        ClassificationResultValidation.Validate(request with { Predictions = [], AbstentionReason = "unsupported-language" }, Profile with { SourceLanguage = "ss" });
    }
}
