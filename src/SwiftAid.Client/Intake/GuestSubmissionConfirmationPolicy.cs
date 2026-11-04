using SwiftAid.Contracts.Intake;

namespace SwiftAid.Client.Intake;

/// <summary>Existing confirmations apply only to the exact current descriptors the reporter reviewed.</summary>
public static class GuestSubmissionConfirmationPolicy
{
    public static bool ProcessingIsCurrent(IntakeConfirmations confirmation, string? reference, string? version) =>
        confirmation.ProcessingConfirmed && !string.IsNullOrWhiteSpace(reference) && !string.IsNullOrWhiteSpace(version) &&
        string.Equals(confirmation.ProcessingPolicyReference, reference, StringComparison.Ordinal) &&
        string.Equals(confirmation.ProcessingPolicyVersion, version, StringComparison.Ordinal);

    public static bool CountryIsCurrent(IntakeConfirmations confirmation, string? country, string? catalogueVersion) =>
        confirmation.OccurrenceCountryConfirmed && !string.IsNullOrWhiteSpace(country) && !string.IsNullOrWhiteSpace(catalogueVersion) &&
        string.Equals(confirmation.OccurrenceCountryCode, country, StringComparison.Ordinal) &&
        string.Equals(confirmation.OccurrenceCountryCatalogueVersion, catalogueVersion, StringComparison.Ordinal);
}
