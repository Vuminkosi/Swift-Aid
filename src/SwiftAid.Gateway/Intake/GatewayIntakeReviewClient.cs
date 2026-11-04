using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Gateway.Intake;

/// <summary>
/// Server-side intake review client for Blazor Server: resolves the current
/// reviewer per call and invokes the intake decision service directly. Reviewer
/// queue reads stay unavailable until a reviewer queue projection exists; the
/// decision POST and promotion path are fully live.
/// </summary>
public sealed class GatewayIntakeReviewClient(
    AuthenticationStateProvider authenticationStateProvider,
    IServiceProvider serviceProvider) : IIntakeReviewClient
{
    private const string Capability = "intake-review";

    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    public Task<UiCapabilityResult<Page<IntakeReviewQueueItem>>> ListAsync(
        IntakeReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query is null || !query.IsValid)
        {
            return Task.FromResult(Invalid<Page<IntakeReviewQueueItem>>("Check the review filters and try again."));
        }

        return Task.FromResult(Unavailable<Page<IntakeReviewQueueItem>>(
            "The reviewer queue is not available yet. Decisions on an open report still apply."));
    }

    public Task<UiCapabilityResult<IntakeReviewDetail>> GetAsync(
        Guid receiptId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (receiptId == Guid.Empty)
        {
            return Task.FromResult(Invalid<IntakeReviewDetail>("Choose a report to review."));
        }

        return Task.FromResult(Unavailable<IntakeReviewDetail>(
            "Report detail for reviewers is not available yet."));
    }

    public async Task<UiCapabilityResult<IntakeReviewOutcome>> ReviewAsync(
        IntakeReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command is null || !command.IsValid)
        {
            return Invalid<IntakeReviewOutcome>("Check the review action and try again.");
        }

        var actor = await GetActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return Denied<IntakeReviewOutcome>();
        }

        var intake = _serviceProvider.GetService<IIntakeService>();
        if (intake is null)
        {
            return Unavailable<IntakeReviewOutcome>("The review service is temporarily unavailable.");
        }

        var kind = command.Decision switch
        {
            IntakeReviewDecision.RouteForReview => IntakeDecisionKind.RouteForReview,
            IntakeReviewDecision.AcceptIntoCase => IntakeDecisionKind.AcceptIntoCase,
            IntakeReviewDecision.Decline => IntakeDecisionKind.Decline,
            _ => (IntakeDecisionKind?)null,
        };

        if (kind is null)
        {
            return Invalid<IntakeReviewOutcome>("Check the review action and try again.");
        }

        IntakeDecisionResult result;
        try
        {
            result = await intake.DecideAsync(
                new IntakeDecision(command.ReceiptId, command.ExpectedVersion, kind.Value, command.ReviewReason),
                actor,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable<IntakeReviewOutcome>("The review could not reach the intake service.");
        }

        return result.Status switch
        {
            IntakeDecisionStatus.Applied when result.Receipt is { } receipt => Success(new IntakeReviewOutcome(
                receipt.ReceiptId,
                receipt.Status.ToString(),
                receipt.RoutingStatus.ToString(),
                ResultingVersion: null,
                PromotedCaseId: result.PromotedCaseId)),
            IntakeDecisionStatus.Applied => Unavailable<IntakeReviewOutcome>(
                "The review result could not be confirmed."),
            IntakeDecisionStatus.NotFoundOrNotAuthorized => Denied<IntakeReviewOutcome>(),
            IntakeDecisionStatus.Conflict => Conflict<IntakeReviewOutcome>(
                "This report changed. Refresh and try again."),
            _ => Invalid<IntakeReviewOutcome>("Check the review action and try again."),
        };
    }

    private async Task<ActorContext?> GetActorAsync()
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = state.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        try
        {
            return ActorContext.FromValidatedPrincipal(principal);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static UiCapabilityResult<T> Success<T>(T value) => new(
        UiCapabilityResultCode.Success,
        value,
        new UiCapabilityMetadata(
            Capability,
            UiCapabilityResults.ContractVersion,
            UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow,
            IsAvailable: true));

    private static UiCapabilityResult<T> Invalid<T>(string message) => new(
        UiCapabilityResultCode.InvalidRequest,
        default,
        UiCapabilityResults.UnavailableMetadata(Capability, message),
        message);

    private static UiCapabilityResult<T> Denied<T>() => new(
        UiCapabilityResultCode.Denied,
        default,
        UiCapabilityResults.UnavailableMetadata(Capability, "This review is not available to the current account."),
        "This review is not available to the current account.");

    private static UiCapabilityResult<T> Unavailable<T>(string message) => new(
        UiCapabilityResultCode.Unavailable,
        default,
        UiCapabilityResults.UnavailableMetadata(Capability, message),
        message);

    private static UiCapabilityResult<T> Conflict<T>(string message) => new(
        UiCapabilityResultCode.Conflict,
        default,
        UiCapabilityResults.UnavailableMetadata(Capability, message),
        message);
}
