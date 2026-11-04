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
    IEllaiUiObservationStore? observations = null)
{
    private const string PublicHelpFeatureId = "public.product-help";
    private const int MaximumUtteranceLength = 8_000;
    private static readonly IReadOnlySet<string> PublicHelpTools =
        new HashSet<string>(StringComparer.Ordinal)
        { EllaiToolNames.ProductFacts, EllaiToolNames.IngestionSchema, EllaiToolNames.ValidateObservations, EllaiToolNames.Locations,
          EllaiToolNames.UiCatalogue, EllaiToolNames.UiObserve, EllaiToolNames.PublicSessions,
          EllaiToolNames.Now, EllaiToolNames.Capabilities, EllaiToolNames.Receipts };

    public async Task<EllaiUtteranceSubmitResult> SubmitAsync(
        string routeSessionId,
        SubmitUtteranceRequest? request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken,
        string interactionMode = "typed")
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

        if (!IsValid(routeSessionId, request) || !EllaiInteractionContext.IsValid(interactionMode))
        {
            return new(EllaiClientOperationStatus.InvalidRequest, null);
        }

        var current = await sessionStore.GetAsync(routeSessionId, cancellationToken).ConfigureAwait(false);
        if (current is null
            || !string.Equals(current.OwnerActorId, actor.ActorId, StringComparison.Ordinal))
        {
            return new(EllaiClientOperationStatus.Unauthorized, null);
        }

        if (!MatchesAuthenticatedEpoch(current.ScopeEpoch, actor.AccessEpoch))
        {
            return new(EllaiClientOperationStatus.PolicyBlocked, null);
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

        // The same server-stamped observation accompanies typed and voice turns.
        // It supplies a referent only; tools must authorize every subsequent read.
        var observation = observations is null ? null :
            await observations.LatestAsync(current.SessionId, cancellationToken).ConfigureAwait(false);
        var currentCaseId = HasWorkspaceScope(current.ScopeEpoch) &&
            observation is { CurrentCaseId: { } id, CurrentNavigationId: EllaiCaseDetailUiManifest.NavigationId } &&
            id != Guid.Empty && observation.ReceivedAtUtc <= DateTimeOffset.UtcNow &&
            observation.ReceivedAtUtc >= DateTimeOffset.UtcNow.AddSeconds(-30)
                ? id.ToString("D") : null;
        var activePage = currentCaseId is not null ? "case-details" : request!.ActivePage;
        var work = new EllaiUtteranceWork(
            current.SessionId,
            current.ScopeEpoch,
            request!.FinalizedText.Trim(),
            PublicHelpFeatureId,
            currentCaseId,
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
            request.Languages,
            EllaiPageContext.DisplayName(activePage) is null ? null : activePage, interactionMode);
        var payload = EllaiUtteranceWorkEnvelope.Serialize(work, request.ClientUtteranceId);
        var safeRequest = request with
        {
            FinalizedText = request.FinalizedText.Trim(),
            TranscriptRef = null,
            InputSource = "typed",
            FeatureId = PublicHelpFeatureId,
            ResourceId = currentCaseId,
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

    private static bool MatchesAuthenticatedEpoch(string scopeEpoch, long accessEpoch)
    {
        var epoch = accessEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (scopeEpoch.StartsWith("access-epoch-", StringComparison.Ordinal))
            return string.Equals(scopeEpoch, "access-epoch-" + epoch, StringComparison.Ordinal);
        if (scopeEpoch.StartsWith("ellai-help-identity-epoch-", StringComparison.Ordinal))
            return string.Equals(scopeEpoch, "ellai-help-identity-epoch-" + epoch, StringComparison.Ordinal);
        // Preserve existing behavior for older or nonstandard session scope labels.
        return true;
    }

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
