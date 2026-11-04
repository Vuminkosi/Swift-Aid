using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Client.Presentation;

/// <summary>Maps the existing authenticated decision POST; reviewer reads remain unavailable.</summary>
public sealed class HttpIntakeReviewClient(HttpClient httpClient) : IIntakeReviewClient
{
    private const string Capability = "intake-review";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<UiCapabilityResult<Page<IntakeReviewQueueItem>>> ListAsync(
        IntakeReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query is null || !query.IsValid)
        {
            return Task.FromResult(Result<Page<IntakeReviewQueueItem>>(
                UiCapabilityResultCode.InvalidRequest,
                null,
                "Check the review filters and try again."));
        }

        return Task.FromResult(UiCapabilityResults.Unavailable<Page<IntakeReviewQueueItem>>(Capability));
    }

    public Task<UiCapabilityResult<IntakeReviewDetail>> GetAsync(
        Guid receiptId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (receiptId == Guid.Empty)
        {
            return Task.FromResult(Result<IntakeReviewDetail>(
                UiCapabilityResultCode.InvalidRequest,
                null,
                "Choose a report to review."));
        }

        return Task.FromResult(UiCapabilityResults.Unavailable<IntakeReviewDetail>(Capability));
    }

    public async Task<UiCapabilityResult<IntakeReviewOutcome>> ReviewAsync(
        IntakeReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command is null || !command.IsValid)
        {
            return Result<IntakeReviewOutcome>(UiCapabilityResultCode.InvalidRequest, null, "Check the review action and try again.");
        }

        if (httpClient.BaseAddress is null)
        {
            return UiCapabilityResults.Unavailable<IntakeReviewOutcome>(Capability);
        }

        var request = new IntakeDecisionApiRequest(
            1,
            command.ExpectedVersion,
            command.Decision.ToString(),
            command.ReviewReason,
            TargetCaseId: null);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync(
                $"api/v1/intake/{command.ReceiptId:D}/decisions",
                request,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<IntakeReviewOutcome>(UiCapabilityResultCode.Cancelled, null, "The review was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return Result<IntakeReviewOutcome>(UiCapabilityResultCode.Retryable, null, "The review timed out. Try again.");
        }
        catch (HttpRequestException)
        {
            return Result<IntakeReviewOutcome>(UiCapabilityResultCode.Retryable, null, "The review could not be reached. Try again.");
        }

        using (response)
        {

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return Result<IntakeReviewOutcome>(UiCapabilityResultCode.Conflict, null, "This report changed. Refresh and try again.");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return Result<IntakeReviewOutcome>(UiCapabilityResultCode.Denied, null, "This review is not available.");
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            return Result<IntakeReviewOutcome>(UiCapabilityResultCode.InvalidRequest, null, "Check the review action and try again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return Result<IntakeReviewOutcome>(UiCapabilityResultCode.Retryable, null, "The review could not be completed. Try again.");
        }

        try
        {
            if (command.Decision == IntakeReviewDecision.AcceptIntoCase)
            {
                var promotion = await response.Content
                    .ReadFromJsonAsync<IntakePromotionApiResponse>(JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (promotion is null || promotion.ReceiptId != command.ReceiptId || promotion.CaseId == Guid.Empty ||
                    promotion.Status != IntakeStatus.AcceptedIntoCase ||
                    promotion.RoutingStatus != IntakeRoutingStatus.RoutedForReview)
                {
                    return Result<IntakeReviewOutcome>(UiCapabilityResultCode.Unavailable, null, "The review result could not be confirmed.");
                }

                return Result<IntakeReviewOutcome>(UiCapabilityResultCode.Success, new IntakeReviewOutcome(
                    promotion.ReceiptId,
                    promotion.Status.ToString(),
                    promotion.RoutingStatus.ToString(),
                    ResultingVersion: null,
                    PromotedCaseId: promotion.CaseId), "Review completed.");
            }

            var receipt = await response.Content
                .ReadFromJsonAsync<IntakeReceipt>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            var expectedStatus = command.Decision switch
            {
                IntakeReviewDecision.RouteForReview => IntakeStatus.AwaitingReview,
                IntakeReviewDecision.Decline => IntakeStatus.Declined,
                _ => (IntakeStatus?)null
            };
            if (receipt is null || receipt.ReceiptId != command.ReceiptId ||
                expectedStatus is null || receipt.Status != expectedStatus ||
                receipt.RoutingStatus != IntakeRoutingStatus.RoutedForReview)
            {
                return Result<IntakeReviewOutcome>(UiCapabilityResultCode.Unavailable, null, "The review result could not be confirmed.");
            }

            return Result<IntakeReviewOutcome>(UiCapabilityResultCode.Success, new IntakeReviewOutcome(
                receipt.ReceiptId,
                receipt.Status.ToString(),
                receipt.RoutingStatus.ToString(),
                ResultingVersion: null), "Review completed.");
        }
        catch (JsonException)
        {
            return Result<IntakeReviewOutcome>(UiCapabilityResultCode.Unavailable, null, "The review result could not be confirmed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<IntakeReviewOutcome>(UiCapabilityResultCode.Cancelled, null, "The review was cancelled.");
        }
        catch (HttpRequestException)
        {
            return Result<IntakeReviewOutcome>(UiCapabilityResultCode.Retryable, null, "The review result could not be confirmed. Refresh to check the latest status.");
        }
        }
    }

    private static UiCapabilityResult<T> Result<T>(UiCapabilityResultCode code, T? value, string message) => new(
        code,
        value,
        new UiCapabilityMetadata(
            Capability,
            UiCapabilityResults.ContractVersion,
            UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow,
            IsAvailable: true),
        message);

    private sealed record IntakeDecisionApiRequest(
        int SchemaVersion,
        long ExpectedVersion,
        string? Decision,
        string? ReviewReason,
        Guid? TargetCaseId);

    private sealed record IntakePromotionApiResponse(
        Guid ReceiptId,
        Guid ClientSubmissionId,
        IntakeStatus Status,
        IntakeRoutingStatus RoutingStatus,
        DateTimeOffset ReceivedAtUtc,
        Guid CaseId);
}
