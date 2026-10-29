using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Browser.Services;

/// <summary>Fails closed until browser storage can durably retain the guest submission state.</summary>
public sealed class BrowserPublicIntakeSubmissionClient : IPublicIntakeSubmissionClient
{
    public Task<UiCapabilityResult<IntakeSubmissionOutcome>> SubmitAsync(
        IntakeSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<IntakeSubmissionOutcome>("public-intake"));
    }
}
