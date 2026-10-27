namespace SwiftAid.Domain.Cases;

/// <summary>Explicit acknowledgements attached to a newly registered report.</summary>
public sealed record ReportConfirmations(
    bool ReportReviewConfirmed,
    string OccurrenceCountryCode,
    string OccurrenceCountryCatalogueVersion,
    bool OccurrenceCountryConfirmed,
    bool ProcessingConfirmed,
    string ProcessingPolicyReference,
    string ProcessingPolicyVersion,
    bool? RecordingConfirmed,
    string? RecordingPolicyReference,
    string? RecordingPolicyVersion)
{
    public void Validate(bool originalAudioPresent)
    {
        if (!ReportReviewConfirmed || !OccurrenceCountryConfirmed || !ProcessingConfirmed)
        {
            throw new ArgumentException("Report review, occurrence country and processing must be explicitly confirmed.");
        }

        RequireText(OccurrenceCountryCode, nameof(OccurrenceCountryCode));
        RequireText(OccurrenceCountryCatalogueVersion, nameof(OccurrenceCountryCatalogueVersion));
        RequireText(ProcessingPolicyReference, nameof(ProcessingPolicyReference));
        RequireText(ProcessingPolicyVersion, nameof(ProcessingPolicyVersion));

        if (originalAudioPresent)
        {
            if (RecordingConfirmed is not true)
            {
                throw new ArgumentException("Original audio requires affirmative recording confirmation.", nameof(RecordingConfirmed));
            }

            RequireText(RecordingPolicyReference, nameof(RecordingPolicyReference));
            RequireText(RecordingPolicyVersion, nameof(RecordingPolicyVersion));
        }
        else if (RecordingConfirmed is not null || RecordingPolicyReference is not null || RecordingPolicyVersion is not null)
        {
            throw new ArgumentException("Recording confirmation metadata must be absent when original audio is not present.");
        }
    }

    private static void RequireText(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty confirmation value is required.", parameterName);
        }
    }
}
