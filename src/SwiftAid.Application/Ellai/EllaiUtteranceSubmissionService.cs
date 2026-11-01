using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>One application boundary for accepting an utterance and queuing
/// its permitted reasoning work. Both the HTTP endpoint and the in-process
/// Blazor Server adapter use this operation so their fences and policy match.</summary>
public sealed class EllaiUtteranceSubmissionService(
    IEllaiSessionStore sessionStore,
    EllaiConsentService consent)
{
    private const string PublicHelpFeatureId = "public.product-help";
    private const int MaximumUtteranceLength = 8_000;
    private static readonly IReadOnlySet<string> PublicHelpTools =
        new HashSet<string>(StringComparer.Ordinal)
        { EllaiToolNames.ProductFacts, EllaiToolNames.IngestionSchema, EllaiToolNames.ValidateObservations, EllaiToolNames.Locations };

    public async Task<EllaiUtteranceSubmitResult> SubmitAsync(
        string routeSessionId,
        SubmitUtteranceRequest? request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        using var admission = EllaiPipelineMetrics.Measure("submission_to_admission");

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

        const string dataClassification = EllaiProcessingClassification.UserProvidedNonSensitive;

        var allowedTools = HasWorkspaceScope(current.ScopeEpoch)
            ? new HashSet<string>(EllaiToolNames.All, StringComparer.Ordinal)
            : PublicHelpTools;

        var work = new EllaiUtteranceWork(
            current.SessionId,
            current.ScopeEpoch,
            request!.FinalizedText.Trim(),
            PublicHelpFeatureId,
            null,
            current.Mode,
            current.InteractionEpoch + 1,
            Guid.NewGuid().ToString("N"),
            actor,
            ContributorEligible: true,
            "consent/0",
            EllaiProcessingClassification.UserProvidedNonSensitive,
            dataClassification,
            allowedTools,
            EllaiConsentService.PublicProductHelpPurpose,
            request.ClientUtteranceId,
            request.Languages);
        var payload = EllaiUtteranceWorkEnvelope.Serialize(work, request.ClientUtteranceId);
        var safeRequest = request with
        {
            FinalizedText = request.FinalizedText.Trim(),
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

    private static bool HasWorkspaceScope(string scopeEpoch) =>
        scopeEpoch.StartsWith("access-epoch-", StringComparison.Ordinal);

    private static bool IsValid(string routeSessionId, SubmitUtteranceRequest? request) =>
        !string.IsNullOrWhiteSpace(routeSessionId)
        && request is not null
        && string.Equals(request.SessionId, routeSessionId, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(request.ClientUtteranceId)
        && !string.IsNullOrWhiteSpace(request.FinalizedText)
        && request.FinalizedText.Length <= MaximumUtteranceLength
        && (request.Languages is null || request.Languages.IsValid())
        && (request.Languages?.InputLanguage is null or "eng" || request.FinalizedText.Length <= 6000)
        && string.Equals(request.InputSource, "typed", StringComparison.Ordinal)
        && (request.FeatureId is null
            || (!string.IsNullOrWhiteSpace(request.FeatureId) && request.FeatureId.Length <= 120))
        && (request.ResourceId is null
            || (!string.IsNullOrWhiteSpace(request.ResourceId) && request.ResourceId.Length <= 120));

}
