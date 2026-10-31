using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>One application boundary for accepting an utterance and queuing
/// its permitted reasoning work. Both the HTTP endpoint and the in-process
/// Blazor Server adapter use this operation so their fences and policy match.</summary>
public sealed class EllaiUtteranceSubmissionService(
    IEllaiSessionStore sessionStore,
    EllaiConsentService consent,
    EllaiPublicHelpClassifier? classifier = null)
{
    private const string PublicHelpFeatureId = "public.product-help";
    private readonly EllaiPublicHelpClassifier _classifier = classifier ?? new EllaiPublicHelpClassifier();

    public async Task<EllaiUtteranceSubmitResult> SubmitAsync(
        string routeSessionId,
        SubmitUtteranceRequest? request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        ActorContext actor;
        try
        {
            actor = ActorContext.FromValidatedPrincipal(principal);
        }
        catch (UnauthorizedAccessException)
        {
            return new(EllaiClientOperationStatus.Unauthorized, null);
        }

        if (!IsValid(routeSessionId, request))
        {
            return new(EllaiClientOperationStatus.InvalidRequest, null);
        }

        var current = await sessionStore.GetAsync(routeSessionId, cancellationToken).ConfigureAwait(false);
        if (current is null
            || !string.Equals(current.OwnerActorId, actor.ActorId, StringComparison.Ordinal))
        {
            return new(EllaiClientOperationStatus.Unauthorized, null);
        }

        if (current.Status != "Active" || current.Mode != EllaiMode.Ask)
        {
            return new(EllaiClientOperationStatus.PolicyBlocked,
                await new EllaiSessionService(sessionStore, consent).GetOwnedViewAsync(
                    routeSessionId, principal, cancellationToken).ConfigureAwait(false));
        }

        if (!_classifier.TryClassify(request!.FinalizedText, out var classification)
            || classification is null)
        {
            return new(EllaiClientOperationStatus.PolicyBlocked, null);
        }

        const string dataClassification = EllaiProcessingClassification.ConsentedNonSensitive;
        var consented = consent.IsGranted(
            routeSessionId,
            EllaiConsentDimension.ReasoningText,
            EllaiConsentService.PublicProductHelpPurpose,
            dataClassification,
            DateTimeOffset.UtcNow);
        if (!consented)
        {
            return new(EllaiClientOperationStatus.PolicyBlocked, null);
        }

        var work = new EllaiUtteranceWork(
            current.SessionId,
            current.ScopeEpoch,
            classification.SanitizedPrompt,
            PublicHelpFeatureId,
            null,
            current.Mode,
            current.InteractionEpoch + 1,
            Guid.NewGuid().ToString("N"),
            actor,
            ContributorEligible: true,
            consent.VersionLabel(routeSessionId),
            EllaiProcessingClassification.ConsentedNonSensitive,
            dataClassification,
            new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.ProductFacts },
            EllaiConsentService.PublicProductHelpPurpose,
            request.ClientUtteranceId);
        var payload = EllaiUtteranceWorkEnvelope.Serialize(work, request.ClientUtteranceId);
        var safeRequest = request with
        {
            FinalizedText = classification.SanitizedPrompt,
            TranscriptRef = null,
            InputSource = "typed",
            FeatureId = PublicHelpFeatureId,
            ResourceId = null,
        };

        var acceptance = await new EllaiSessionService(sessionStore, consent)
            .SubmitUtteranceAsync(safeRequest, actor, cancellationToken, payload)
            .ConfigureAwait(false);
        if (acceptance is null)
        {
            var currentView = await new EllaiSessionService(sessionStore, consent).GetOwnedViewAsync(
                routeSessionId, principal, cancellationToken).ConfigureAwait(false);
            return new(EllaiClientOperationStatus.Conflict, currentView);
        }

        return new(EllaiClientOperationStatus.Accepted, acceptance.View);
    }

    private static bool IsValid(string routeSessionId, SubmitUtteranceRequest? request) =>
        !string.IsNullOrWhiteSpace(routeSessionId)
        && request is not null
        && string.Equals(request.SessionId, routeSessionId, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(request.ClientUtteranceId)
        && !string.IsNullOrWhiteSpace(request.FinalizedText)
        && request.FinalizedText.Length <= EllaiPublicHelpClassifier.MaximumInputLength
        && string.Equals(request.InputSource, "typed", StringComparison.Ordinal)
        && (request.FeatureId is null
            || (!string.IsNullOrWhiteSpace(request.FeatureId) && request.FeatureId.Length <= 120))
        && (request.ResourceId is null
            || (!string.IsNullOrWhiteSpace(request.ResourceId) && request.ResourceId.Length <= 120));

}
