using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

public static class EllaiProcessingClassification
{
    public const string UnknownOrigin = "unknown-origin";
    public const string RealSensitive = "real-sensitive";
    public const string SyntheticDemo = "synthetic-demo";
    public const string ConsentedNonSensitive = "consented-nonsensitive";
}

/// <summary>Effective-authority request for one Ellai operation.</summary>
public sealed record EllaiAuthorizationRequest(
    string SessionId,
    EllaiMode Mode,
    string FeatureId,
    string? ResourceId,
    UiIntentKind? IntentKind,
    AdminActionKind? ActionKind,
    string ScopeEpoch,
    string ConsentVersion,
    bool ContributorEligible);

/// <summary>Authorization outcome: permitted bounded context or typed denial.</summary>
public sealed record EllaiAuthorizationResult(
    bool Permitted,
    string? DenialReason,
    string PolicyVersion,
    string ScopeEpoch);

/// <summary>Composes identity/resource/sensitivity grants with session scope,
/// release profile, consent destination and action class. Denies by default;
/// a microphone grant never authorizes Contributor dispatch and
/// real-sensitive content never routes to Contributor. (ELL-T03 groundwork.)</summary>
public sealed class EllaiPolicyService
{
    public const string PolicyVersion = "ellai-policy/1.3";

    private static readonly IReadOnlySet<string> ClinicalKeywords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "diagnos", "triage", "symptom", "treatment", "dosage", "dose",
            "prescri", "outbreak", "prognos", "clinical"
        };

    public EllaiAuthorizationResult Authorize(EllaiAuthorizationRequest request, string evidenceClassification)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Deny("Missing session scope.");
        }

        if (string.IsNullOrWhiteSpace(request.ScopeEpoch))
        {
            return Deny("Missing scope epoch.");
        }

        if (IsClinicalText(request.FeatureId) || IsClinicalText(evidenceClassification))
        {
            return Deny("Clinical content is excluded from Ellai authority.");
        }

        if (request.ActionKind is not null && !Enum.IsDefined(request.ActionKind.Value))
        {
            return Deny("Unknown administrative action kind.");
        }

        if (request.IntentKind is UiIntentKind.StartGuidance or UiIntentKind.ControlGuidance
            && !string.Equals(request.FeatureId, "ellai.guidance", StringComparison.Ordinal))
        {
            return Deny("Guidance intents require featureId ellai.guidance.");
        }

        if (!request.ContributorEligible && request.ActionKind is not null)
        {
            return Deny("Contributor-ineligible content cannot back an administrative plan.");
        }

        return new EllaiAuthorizationResult(true, null, PolicyVersion, request.ScopeEpoch);
    }

    /// <summary>Contributor route is restricted to approved synthetic/non-sensitive
    /// inputs; unknown provenance or real-sensitive content is ineligible.</summary>
    public static bool IsContributorEligible(string processingLabel, string dataClassification)
    {
        return (string.Equals(processingLabel, EllaiProcessingClassification.SyntheticDemo, StringComparison.Ordinal)
                && string.Equals(dataClassification, EllaiProcessingClassification.SyntheticDemo, StringComparison.Ordinal))
            || (string.Equals(processingLabel, EllaiProcessingClassification.ConsentedNonSensitive, StringComparison.Ordinal)
                && string.Equals(dataClassification, EllaiProcessingClassification.ConsentedNonSensitive, StringComparison.Ordinal));
    }

    private static bool IsClinicalText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return ClinicalKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private EllaiAuthorizationResult Deny(string reason) =>
        new(false, reason, PolicyVersion, string.Empty);
}
