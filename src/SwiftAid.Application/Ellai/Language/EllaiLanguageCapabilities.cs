namespace SwiftAid.Application.Ellai;

/// <summary>Validation state of one conversational language. Only
/// <see cref="Verified"/> languages may back voice or translation claims;
/// <see cref="Unvalidated"/> languages are spec targets, not capabilities,
/// and <see cref="Unsupported"/> languages have no valid provider mapping at
/// all (for example Ndebele has no NLLB code and none is invented).</summary>
public enum EllaiLanguageValidationStatus
{
    Verified,
    Unvalidated,
    Unsupported,
}

/// <summary>Canonical per-language conversational capability. Statuses follow
/// the governing spec (English first; Shona/siSwati are targets subject to
/// measured ASR/translation quality) and the Ellai native-voice design
/// constraints: no silent provider substitution, no invented language codes,
/// English fallback with the original report preserved.</summary>
public sealed record EllaiLanguageCapability(
    string Iso639_3,
    string DisplayName,
    string? NllbCode,
    string AsrProvider,
    bool AsrAvailable,
    bool TranslationAvailable,
    string? TtsCheckpoint,
    string ResponseTextLanguage,
    string ResponseSpeechLanguage,
    bool TextInputAvailable,
    bool VoiceInputAvailable,
    EllaiLanguageValidationStatus Status,
    string FallbackBehavior,
    string Notes);

/// <summary>Canonical language capability registry for Ellai conversation.
/// English uses the existing Muse Voice Transcribe / Muse Spark / MMS-TTS
/// path with no translation. Every other language is explicitly unavailable
/// until its ASR, translation and TTS providers are independently validated;
/// callers must surface the fallback instead of fabricating support.</summary>
public static class EllaiLanguageCapabilityRegistry
{
    public static IReadOnlyList<EllaiLanguageCapability> All { get; } =
    [
        new(
            "eng",
            "English",
            "eng_Latn",
            "Muse Voice Transcribe",
            true,
            false,
            "facebook/mms-tts-eng",
            "eng",
            "eng",
            true,
            true,
            EllaiLanguageValidationStatus.Verified,
            "none",
            "Existing lane. English bypasses translation entirely."),
        new(
            "sna",
            "Shona",
            "sna_Latn",
            "unvalidated",
            false,
            false,
            null,
            "eng",
            "eng",
            true,
            false,
            EllaiLanguageValidationStatus.Unvalidated,
            "english-text",
            "Target language. Native ASR, translation and TTS are unvalidated; free-text entry is preserved as the original until validated."),
        new(
            "nya",
            "Nyanja",
            "nya_Latn",
            "unvalidated",
            false,
            false,
            null,
            "eng",
            "eng",
            true,
            false,
            EllaiLanguageValidationStatus.Unvalidated,
            "english-text",
            "Target language. Native ASR, translation and TTS are unvalidated; free-text entry is preserved as the original until validated."),
        new(
            "tso",
            "Tsonga",
            "tso_Latn",
            "unvalidated",
            false,
            false,
            null,
            "eng",
            "eng",
            true,
            false,
            EllaiLanguageValidationStatus.Unvalidated,
            "english-text",
            "Target language. Native ASR, translation and TTS are unvalidated; free-text entry is preserved as the original until validated."),
        new(
            "ssw",
            "siSwati",
            "ssw_Latn",
            "unvalidated",
            false,
            false,
            null,
            "eng",
            "eng",
            true,
            false,
            EllaiLanguageValidationStatus.Unvalidated,
            "english-text",
            "Target language. NLLB carries a code but the native ASR path is independently unvalidated, so native voice stays disabled."),
        new(
            "nde",
            "Ndebele",
            null,
            "unvalidated",
            false,
            false,
            null,
            "eng",
            "eng",
            true,
            false,
            EllaiLanguageValidationStatus.Unsupported,
            "english-text",
            "NLLB-200 1.3B lists no Ndebele code; no token is invented and no silent mapping to another language is applied."),
    ];

    public static EllaiLanguageCapability? Find(string iso639_3) =>
        All.FirstOrDefault(c => string.Equals(c.Iso639_3, iso639_3, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when routing this language through reasoning would need
    /// a validated native-to-English translation first. English never does.</summary>
    public static bool RequiresTranslationForReasoning(string iso639_3) =>
        !string.Equals(iso639_3, "eng", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the validated NLLB code for translation, or null when
    /// none exists. Never invents a code: unknown and Ndebele inputs yield
    /// null instead of a guessed mapping.</summary>
    public static string? ResolveNllbCode(string iso639_3) => Find(iso639_3)?.NllbCode;

    /// <summary>True only for the verified English lane. Everything else must
    /// take the explicit fallback path.</summary>
    public static bool IsVoiceReady(string iso639_3) =>
        Find(iso639_3) is { Status: EllaiLanguageValidationStatus.Verified, VoiceInputAvailable: true };
}

/// <summary>Configuration policy for the experimental NLLB multilingual
/// classification bridge. Disabled by default in every environment; enabling
/// additionally requires research-only mode, mandatory human review, and a
/// ban on automatic outbreak confirmation, consequential automation and
/// unreviewed surveillance signals. A flag alone never overrides model
/// licensing or production deployment authorization. This declaration is not
/// wired to a translation admission service; environment, organization and
/// validated-provider checks must be enforced there before enabling inference.</summary>
public sealed record ExperimentalMultilingualIntelligenceOptions(
    bool Enabled = false,
    bool ResearchModeOnly = true,
    string TranslationProvider = "ExistingNllbWorker",
    string TargetLanguage = "eng_Latn",
    bool RequireHumanReview = true,
    bool AllowAutomaticOutbreakConfirmation = false,
    bool AllowConsequentialAutomation = false,
    bool AllowUnreviewedSurveillanceSignals = false)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enabled)
        {
            return errors;
        }

        if (!ResearchModeOnly)
        {
            errors.Add("Experimental multilingual intelligence requires research-only mode.");
        }

        if (!RequireHumanReview)
        {
            errors.Add("Experimental multilingual intelligence requires mandatory human review.");
        }

        if (AllowAutomaticOutbreakConfirmation)
        {
            errors.Add("Experimental multilingual intelligence must not confirm outbreaks automatically.");
        }

        if (AllowConsequentialAutomation)
        {
            errors.Add("Experimental multilingual intelligence must not drive consequential automation.");
        }

        if (AllowUnreviewedSurveillanceSignals)
        {
            errors.Add("Experimental multilingual intelligence must not emit unreviewed surveillance signals.");
        }

        if (string.IsNullOrWhiteSpace(TranslationProvider) || string.IsNullOrWhiteSpace(TargetLanguage))
        {
            errors.Add("Experimental multilingual intelligence requires an explicit provider and target language.");
        }

        return errors;
    }

    /// <summary>Fail-closed admission: only an enabled, research-only,
    /// human-reviewed configuration with no consequential automation admits
    /// new translation work. Disabling stops admissions; history is kept.</summary>
    public static bool AllowsExperimentalAdmission(ExperimentalMultilingualIntelligenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Enabled
            && options.ResearchModeOnly
            && options.RequireHumanReview
            && !options.AllowAutomaticOutbreakConfirmation
            && !options.AllowConsequentialAutomation
            && !options.AllowUnreviewedSurveillanceSignals;
    }
}
