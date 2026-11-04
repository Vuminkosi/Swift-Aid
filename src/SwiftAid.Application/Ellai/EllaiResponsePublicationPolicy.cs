using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Allows a completed response to reach its owner-facing session
/// stream when the reasoning lane was validly authorized, including ordinary
/// typed guest messages that intentionally require no separate consent grant.</summary>
public static class EllaiResponsePublicationPolicy
{
    public static bool CanPublish(EllaiUtteranceWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!string.Equals(work.ConsentPurpose, EllaiConsentPurposes.PublicProductHelp, StringComparison.Ordinal))
        {
            return false;
        }

        return (string.Equals(work.ProcessingLabel,
                    EllaiProcessingClassification.UserProvidedNonSensitive, StringComparison.Ordinal)
                && string.Equals(work.DataClassification,
                    EllaiProcessingClassification.UserProvidedNonSensitive, StringComparison.Ordinal))
            || (string.Equals(work.ProcessingLabel,
                    EllaiProcessingClassification.ConsentedNonSensitive, StringComparison.Ordinal)
                && string.Equals(work.DataClassification,
                    EllaiProcessingClassification.ConsentedNonSensitive, StringComparison.Ordinal));
    }
}
