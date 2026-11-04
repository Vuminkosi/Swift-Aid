using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Cases;

public sealed class FollowUpReviewGatewayClient(AuthenticationStateProvider authentication, IServiceProvider services)
    : IFollowUpRecommendationReviewClient
{
    public async Task<FollowUpReviewResult> ReviewAsync(Guid caseId, Guid recommendationId, Guid commandId,
        CancellationToken cancellationToken = default)
    {
        var principal = (await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        if (principal.Identity?.IsAuthenticated != true) return new(FollowUpReviewStatus.Denied);
        try
        {
            var actor = ActorContext.FromValidatedPrincipal(principal);
            await using var scope = services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetService<IFollowUpRecommendationReviewService>();
            if (service is null) return new(FollowUpReviewStatus.Unavailable);
            var result = await service.ReviewAsync(actor, caseId, recommendationId, commandId, cancellationToken).ConfigureAwait(false);
            return new(Map(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return new(FollowUpReviewStatus.Denied); }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(FollowUpReviewStatus.Unavailable); }
    }

    internal static FollowUpReviewStatus Map(FollowUpRecommendationReviewOutcome outcome) => outcome switch
    {
        FollowUpRecommendationReviewOutcome.Recorded => FollowUpReviewStatus.Recorded,
        FollowUpRecommendationReviewOutcome.Replayed => FollowUpReviewStatus.Replayed,
        FollowUpRecommendationReviewOutcome.AlreadyReviewed => FollowUpReviewStatus.AlreadyReviewed,
        FollowUpRecommendationReviewOutcome.Stale => FollowUpReviewStatus.Stale,
        FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized => FollowUpReviewStatus.Denied,
        FollowUpRecommendationReviewOutcome.Conflict => FollowUpReviewStatus.Conflict,
        _ => FollowUpReviewStatus.Unavailable
    };
}
