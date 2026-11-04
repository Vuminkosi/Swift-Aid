using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
public sealed class EllaiLanguageCapabilityTests
{
    [Fact]
    public void EnglishBypassesTranslationOnTheExistingLane()
    {
        var english = EllaiLanguageCapabilityRegistry.Find("eng");

        Assert.NotNull(english);
        Assert.Equal(EllaiLanguageValidationStatus.Verified, english.Status);
        Assert.False(english.TranslationAvailable);
        Assert.False(EllaiLanguageCapabilityRegistry.RequiresTranslationForReasoning("eng"));
        Assert.True(EllaiLanguageCapabilityRegistry.IsVoiceReady("eng"));
        Assert.True(english is { TextInputAvailable: true, VoiceInputAvailable: true });
    }

    [Fact]
    public void NativeTargetsAreExplicitlyUnavailableWithEnglishFallback()
    {
        foreach (var code in new[] { "sna", "nya", "tso", "ssw" })
        {
            var capability = EllaiLanguageCapabilityRegistry.Find(code);

            Assert.NotNull(capability);
            Assert.Equal(EllaiLanguageValidationStatus.Unvalidated, capability.Status);
            Assert.False(capability.AsrAvailable);
            Assert.False(capability.TranslationAvailable);
            Assert.False(capability.VoiceInputAvailable);
            Assert.False(EllaiLanguageCapabilityRegistry.IsVoiceReady(code));
            Assert.True(EllaiLanguageCapabilityRegistry.RequiresTranslationForReasoning(code));
            Assert.Equal("eng", capability.ResponseTextLanguage);
            Assert.Equal("eng", capability.ResponseSpeechLanguage);
            Assert.Equal("english-text", capability.FallbackBehavior);
        }
    }

    [Fact]
    public void NdebeleHasNoInventedNllbCode()
    {
        var ndebele = EllaiLanguageCapabilityRegistry.Find("nde");

        Assert.NotNull(ndebele);
        Assert.Equal(EllaiLanguageValidationStatus.Unsupported, ndebele.Status);
        Assert.Null(ndebele.NllbCode);
        Assert.Null(EllaiLanguageCapabilityRegistry.ResolveNllbCode("nde"));
        Assert.Null(EllaiLanguageCapabilityRegistry.ResolveNllbCode("und"));
    }

    [Fact]
    public void SupportedCodesResolveWithoutSubstitution()
    {
        Assert.Equal("sna_Latn", EllaiLanguageCapabilityRegistry.ResolveNllbCode("sna"));
        Assert.Equal("ssw_Latn", EllaiLanguageCapabilityRegistry.ResolveNllbCode("ssw"));
        Assert.Equal("eng_Latn", EllaiLanguageCapabilityRegistry.ResolveNllbCode("eng"));
    }

    [Fact]
    public void ExperimentalBridgeIsDisabledByDefaultAndFailsClosed()
    {
        var options = new ExperimentalMultilingualIntelligenceOptions();

        Assert.False(options.Enabled);
        Assert.Empty(options.Validate());
        Assert.False(ExperimentalMultilingualIntelligenceOptions.AllowsExperimentalAdmission(options));
    }

    [Fact]
    public void ExperimentalBridgeRejectsConsequentialAutomation()
    {
        var lax = new ExperimentalMultilingualIntelligenceOptions(
            Enabled: true,
            AllowAutomaticOutbreakConfirmation: true,
            AllowConsequentialAutomation: true,
            AllowUnreviewedSurveillanceSignals: true);

        Assert.NotEmpty(lax.Validate());
        Assert.False(ExperimentalMultilingualIntelligenceOptions.AllowsExperimentalAdmission(lax));

        var strict = new ExperimentalMultilingualIntelligenceOptions(Enabled: true);

        Assert.Empty(strict.Validate());
        Assert.True(ExperimentalMultilingualIntelligenceOptions.AllowsExperimentalAdmission(strict));
    }
}
