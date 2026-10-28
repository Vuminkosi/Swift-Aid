using SwiftAid.Contracts.Intake;

namespace SwiftAid.Client.Presentation.PublicWorkflows;

/// <summary>Guest submission capability for public surfaces. A receipt is present only for live server success.</summary>
public enum IntakeSubmissionOutcomeKind
{
    Received,
    SavedLocally,
    QueuedLocally
}

public sealed record IntakeSubmissionOutcome(
    IntakeSubmissionOutcomeKind Kind,
    Guid ClientSubmissionId,
    IntakeReceipt? Receipt,
    string Message);

public interface IPublicIntakeSubmissionClient
{
    Task<UiCapabilityResult<IntakeSubmissionOutcome>> SubmitAsync(
        IntakeSubmission submission,
        CancellationToken cancellationToken = default);
}

/// <summary>Versioned country and policy metadata used to validate a public report form.</summary>
public interface IPublicIntakeMetadataClient
{
    Task<UiCapabilityResult<PublicIntakeMetadata>> GetMetadataAsync(
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<PublicIntakeCountryCatalogue>> GetCountriesAsync(
        CancellationToken cancellationToken = default);
}
