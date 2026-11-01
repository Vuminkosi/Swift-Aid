using SwiftAid.Application.Classification;

namespace SwiftAid.Application.Tests;

public sealed class TranslationIdentityTests
{
    [Fact]
    public void FingerprintReusesNormalizedContentButNeverCrossesAuthorizationScope()
    {
        var first = TranslationIdentity.Fingerprint("org", "workspace", "standard", "two  reports", "sna");
        Assert.Equal(first, TranslationIdentity.Fingerprint("org", "workspace", "standard", "two reports", "sna"));
        Assert.NotEqual(first, TranslationIdentity.Fingerprint("other", "workspace", "standard", "two reports", "sna"));
        Assert.NotEqual(first, TranslationIdentity.Fingerprint("org", "other", "standard", "two reports", "sna"));
        Assert.NotEqual(first, TranslationIdentity.Fingerprint("org", "workspace", "restricted", "two reports", "sna"));
    }

    [Theory]
    [InlineData("nd")]
    [InlineData("nde")]
    [InlineData("und")]
    public void UnsupportedLanguageNeverGetsASubstitute(string language) => Assert.Null(TranslationIdentity.Language(language));

    [Fact]
    public void QualityFindingsNeverGrantApprovalAndDetectChangedNumbers()
    {
        Assert.Contains("numeric-content-changed", TranslationIdentity.Assess("2 5", "2 6"));
        Assert.Equal(["human-language-review-required"], TranslationIdentity.Assess("2 reports", "2 reports"));
    }
}
