using SwiftAid.Application.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Intake;
using SwiftAid.Gateway.Intake;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Server-side public intake submission for Blazor Server: delegates to the circuit-scoped web intake service.</summary>
public sealed class WebPublicIntakeSubmissionClient(WebGuestIntakeService intake) : IPublicIntakeSubmissionClient
{
    private readonly WebGuestIntakeService _intake = intake ?? throw new ArgumentNullException(nameof(intake));

    public async Task<UiCapabilityResult<IntakeSubmissionOutcome>> SubmitAsync(
        IntakeSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        try
        {
            var receipt = await _intake.SubmitAsync(submission, cancellationToken).ConfigureAwait(false);
            return new UiCapabilityResult<IntakeSubmissionOutcome>(
                UiCapabilityResultCode.Success,
                new IntakeSubmissionOutcome(
                    IntakeSubmissionOutcomeKind.Received,
                    submission.ClientSubmissionId,
                    receipt,
                    "Report received for review."),
                LiveMetadata("public-intake-submission"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IntakeSubmissionException exception) when (exception.Code == IntakeErrorCode.InvalidReport)
        {
            return new UiCapabilityResult<IntakeSubmissionOutcome>(
                UiCapabilityResultCode.InvalidRequest,
                null,
                UiCapabilityResults.UnavailableMetadata(
                    "public-intake-submission", "The report was rejected, but the service did not identify a field. Review the details and try again."),
                "The report was rejected, but the service did not identify a field. Review the details and try again.");
        }
        catch (IntakeSubmissionException exception) when (exception.Code == IntakeErrorCode.SubmissionConflict)
        {
            return new UiCapabilityResult<IntakeSubmissionOutcome>(
                UiCapabilityResultCode.Conflict, null,
                UiCapabilityResults.UnavailableMetadata("public-intake-submission", "The report identifier was already used with different details."),
                "The report identifier was already used with different details.");
        }
        catch (IntakeSubmissionException exception) when (exception.Code is IntakeErrorCode.RateLimited
            or IntakeErrorCode.PayloadTooLarge)
        {
            return new UiCapabilityResult<IntakeSubmissionOutcome>(
                UiCapabilityResultCode.Retryable,
                null,
                UiCapabilityResults.UnavailableMetadata(
                    "public-intake-submission", "Please wait and try submitting again."),
                "Please wait and try submitting again.");
        }
        catch (Exception)
        {
            return UiCapabilityResults.Unavailable<IntakeSubmissionOutcome>("public-intake-submission");
        }
    }

    private static UiCapabilityMetadata LiveMetadata(string capability) => new(
        capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        IsAvailable: true);
}
