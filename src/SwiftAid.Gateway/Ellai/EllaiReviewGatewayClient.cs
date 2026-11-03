using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Server-side review client for Blazor Server: resolves the current
/// principal per call and delegates to the in-process journal, coordinator
/// and approval service. Mirrors the HTTP endpoint semantics exactly;
/// unavailable dependencies read as unavailable, never as fabricated data.
/// Guests have no approval reviews: unauthenticated calls return empty.</summary>
public sealed class EllaiReviewGatewayClient(
    AuthenticationStateProvider authenticationStateProvider,
    IServiceProvider serviceProvider,
    EllaiGuestSessionIdentity? guestSessionIdentity = null) : IEllaiReviewClient
{
    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly EllaiGuestSessionIdentity _guestSessionIdentity =
        guestSessionIdentity ?? new EllaiGuestSessionIdentity();

    public Task<IReadOnlyList<EllaiEnglishVoiceDto>> ListEnglishVoicesAsync(CancellationToken cancellationToken) =>
        _serviceProvider.GetRequiredService<IEllaiSpeechSynthesisClient>().ListEnglishVoicesAsync(cancellationToken);

    public async Task<EllaiSessionHistoryPageDto?> ListSessionHistoryAsync(int offset, CancellationToken cancellationToken)
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = state.User;
        if (principal.Identity?.IsAuthenticated != true
            && !_guestSessionIdentity.TryGetHistoryPrincipal(out principal)) return new([], null);
        var actor = ActorContext.FromValidatedPrincipal(principal);
        await using var historyScope = _serviceProvider.CreateAsyncScope();
        return await EllaiConversationFeed.ListOwnedSessionsAsync(
            historyScope.ServiceProvider.GetRequiredService<IEllaiSessionStore>(), actor.ActorId, Math.Max(0, offset), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EllaiSessionStartResult> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = ResolvePrincipal(state.User, sessionId);
        if (principal is null) return new(EllaiClientOperationStatus.Unauthorized);
        await using var resumeScope = _serviceProvider.CreateAsyncScope();
        var session = await resumeScope.ServiceProvider.GetRequiredService<EllaiSessionService>()
            .GetOwnedViewAsync(sessionId, principal, cancellationToken).ConfigureAwait(false);
        return session is null ? new(EllaiClientOperationStatus.Unauthorized)
            : new(EllaiClientOperationStatus.Accepted, session);
    }

    public async Task<EllaiSessionStartResult> StartSessionAsync(
        EllaiMode mode, CancellationToken cancellationToken)
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var isAuthenticated = state.User?.Identity?.IsAuthenticated == true;
        if (!isAuthenticated && mode != EllaiMode.Ask)
        {
            return new(EllaiClientOperationStatus.Unauthorized);
        }

        var principal = state.User;
        if (principal is null)
        {
            return new(EllaiClientOperationStatus.Unauthorized);
        }
        ActorContext actor;
        try
        {
            if (!isAuthenticated)
            {
                principal = _guestSessionIdentity.CreatePrincipal();
            }
            actor = ActorContext.FromValidatedPrincipal(principal);
        }
        catch (UnauthorizedAccessException)
        {
            return new(EllaiClientOperationStatus.Unauthorized);
        }

        var scopeResolver = _serviceProvider.GetService<EllaiSessionScopeResolver>();
        if (scopeResolver is null)
        {
            return new(EllaiClientOperationStatus.Unavailable);
        }

        try
        {
            var scope = isAuthenticated
                ? await scopeResolver.GetAsync(actor, cancellationToken).ConfigureAwait(false)
                : new SwiftAid.Contracts.Sync.ScopeManifest(
                    Array.Empty<SwiftAid.Contracts.Sync.ScopeGrantManifest>(),
                    actor.AccessEpoch,
                    DateTimeOffset.UtcNow.AddHours(2));
            var sessions = _serviceProvider.GetService<EllaiSessionService>();
            if (sessions is null)
            {
                return new(EllaiClientOperationStatus.Unavailable);
            }

            var session = await sessions.StartAsync(
                new StartSessionRequest(
                    mode, Guid.NewGuid().ToString("N"), null, null, null, null),
                principal,
                scope,
                cancellationToken).ConfigureAwait(false);
            if (!isAuthenticated)
            {
                _guestSessionIdentity.Bind(session.SessionId, principal, scope.ExpiresAtUtc);
            }
            return new(EllaiClientOperationStatus.Accepted, session);
        }
        catch (OperationCanceledException)
        {
            return new(EllaiClientOperationStatus.OutcomeUnknown);
        }
        catch (UnauthorizedAccessException)
        {
            return new(EllaiClientOperationStatus.Unauthorized);
        }
        catch (ArgumentException)
        {
            return new(EllaiClientOperationStatus.InvalidRequest);
        }
        catch (ScopeUnavailableException)
        {
            return new(EllaiClientOperationStatus.PolicyBlocked);
        }
        catch (Exception)
        {
            // The session store may have accepted the write before an error
            // interrupted the response. Keep this distinct from preflight
            // denials so the UI can prevent an unsafe duplicate start.
            return new(EllaiClientOperationStatus.OutcomeUnknown);
        }
    }

    public async Task<EllaiUtteranceSubmitResult> SubmitUtteranceAsync(
        SubmitUtteranceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var state = await _authenticationStateProvider
                .GetAuthenticationStateAsync().ConfigureAwait(false);
            var principal = ResolvePrincipal(state.User, request?.SessionId ?? string.Empty);
            if (principal is null)
            {
                return new(EllaiClientOperationStatus.Unauthorized, null);
            }
            var submissions = _serviceProvider.GetService<EllaiUtteranceSubmissionService>();
            if (submissions is null)
            {
                return new(EllaiClientOperationStatus.Unavailable, null);
            }
            return await submissions.SubmitAsync(request?.SessionId ?? string.Empty,
                request, principal, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new(EllaiClientOperationStatus.Unavailable, null);
        }
    }

    public async Task<bool> GrantPublicProductHelpConsentAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
            var principal = ResolvePrincipal(state.User, sessionId);
            if (principal is null)
            {
                return false;
            }
            var sessions = _serviceProvider.GetService<EllaiSessionService>();
            var consent = _serviceProvider.GetService<EllaiConsentService>();
            var consentStore = _serviceProvider.GetService<IEllaiConsentStore>();
            if (sessions is null || consent is null || consentStore is null
                || await sessions.GetOwnedViewAsync(sessionId, principal, cancellationToken)
                    .ConfigureAwait(false) is not { Status: "Active" })
            {
                return false;
            }

            var grant = consent.Grant(
                sessionId,
                EllaiConsentDimension.ReasoningText,
                EllaiConsentPurposes.PublicProductHelp,
                EllaiProcessingClassification.ConsentedNonSensitive,
                TimeSpan.FromHours(1));
            try
            {
                await consentStore.RecordAsync(grant, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch
            {
                consent.Revoke(grant.GrantId);
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> GrantVoiceCaptureConsentAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
            var principal = ResolvePrincipal(state.User, sessionId);
            var sessions = _serviceProvider.GetService<EllaiSessionService>();
            var voice = _serviceProvider.GetService<EllaiVoiceTranscriptionService>();
            if (principal is null || sessions is null || voice is null
                || await sessions.GetOwnedViewAsync(sessionId, principal, cancellationToken)
                    .ConfigureAwait(false) is not { Status: "Active" })
            {
                return false;
            }
            return await voice.GrantVoiceConsentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task<EllaiVoiceTranscriptionResult?> TranscribeVoiceAsync(
        string sessionId, byte[] audio, EllaiVoiceCaptureMode mode, CancellationToken cancellationToken,
        string? voiceCaptureId = null, long voiceCaptureOffsetSamples = 0)
    {
        if (audio is null || audio.Length == 0 || audio.Length > EllaiVoiceTranscriptionService.MaximumAudioBytes
            || !await OwnsSessionAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        try
        {
            var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
            var principal = ResolvePrincipal(state.User, sessionId);
            var sessions = _serviceProvider.GetService<EllaiSessionService>();
            var voice = _serviceProvider.GetService<EllaiVoiceTranscriptionService>();
            if (principal is null || sessions is null || voice is null
                || await sessions.GetOwnedViewAsync(sessionId, principal, cancellationToken)
                    .ConfigureAwait(false) is not { Status: "Active" })
            {
                return null;
            }
            using var stream = new MemoryStream(audio, writable: false);
            return await voice.TranscribeAsync(
                    sessionId, mode, stream, audio.LongLength, cancellationToken,
                    voiceCaptureId, voiceCaptureOffsetSamples, submissionPrincipal: principal)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<EllaiVoiceTurnStatusDto?> GetVoiceTurnStatusAsync(
        string sessionId, CancellationToken cancellationToken, long afterSequence = 0)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || !await OwnsSessionAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var coordinator = _serviceProvider.GetService<EllaiVoiceTurnCoordinator>();
        if (coordinator is null)
        {
            return null;
        }

        var snapshot = coordinator.GetSnapshot(sessionId);
        return new EllaiVoiceTurnStatusDto(snapshot.State.ToString(), snapshot.Epoch,
            ToMilliseconds(snapshot.ConfirmedSpeech), ToMilliseconds(snapshot.Silence),
            snapshot.PartialTranscript,
            coordinator.GetCompletedTurns(sessionId, afterSequence)
                .TakeWhile(turn => !turn.ServerSubmission || turn.Admission is not null)
                .Select(turn => new EllaiCompletedVoiceTurnDto(
                    turn.Sequence,
                    turn.ProviderTurnId,
                    turn.Transcript,
                    sessionId,
                    turn.TurnId.ToString("N"),
                    turn.StreamEpoch,
                    AudioStartSample: turn.AudioStartSample,
                    AudioEndSample: turn.AudioEndSample,
                    VoiceCaptureId: turn.VoiceCaptureId,
                    ServerSubmission: turn.ServerSubmission,
                    Admission: turn.Admission))
                .ToArray(),
            snapshot.SessionId);
    }

    private static int ToMilliseconds(TimeSpan duration) =>
        (int)Math.Clamp(duration.TotalMilliseconds, 0, int.MaxValue);

    public async Task<EllaiSessionCancelResult> CancelSessionAsync(
        string sessionId, long expectedSessionVersion, CancellationToken cancellationToken)
    {
        try
        {
            var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
            var principal = ResolvePrincipal(state.User, sessionId);
            if (principal is null)
            {
                return new(EllaiClientOperationStatus.Unauthorized);
            }
            var sessions = _serviceProvider.GetService<EllaiSessionService>();
            if (sessions is null)
            {
                return new(EllaiClientOperationStatus.Unavailable);
            }
            var cancelled = await sessions.CancelAsync(
                sessionId, expectedSessionVersion, principal, cancellationToken).ConfigureAwait(false);
            if (cancelled)
            {
                var now = DateTimeOffset.UtcNow;
                EllaiSessionCancellationEffects.Apply(
                    sessionId,
                    now,
                    _serviceProvider.GetService<EllaiApprovalDialogCoordinator>(),
                    _serviceProvider.GetService<EllaiSessionEventJournal>(),
                    _serviceProvider.GetService<EllaiSessionResponseStore>());
                return new(EllaiClientOperationStatus.Cancelled);
            }
            var current = await sessions.GetOwnedViewAsync(sessionId, principal, cancellationToken)
                .ConfigureAwait(false);
            return current is null
                ? new(EllaiClientOperationStatus.Unauthorized)
                : new(EllaiClientOperationStatus.Conflict, current);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { return new(EllaiClientOperationStatus.Unauthorized); }
        catch (Exception) { return new(EllaiClientOperationStatus.Unavailable); }
    }

    public async Task<EllaiSessionEventPageDto?> PollEventsAsync(
        string sessionId, long cursor, int waitSeconds, CancellationToken cancellationToken)
    {
        if (!await OwnsSessionAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var journals = _serviceProvider.GetService<EllaiSessionEventJournal>();
        if (journals is null)
        {
            return null;
        }

        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 0, 30));
            while (true)
            {
                var page = journals.ReadSince(sessionId, Math.Max(0, cursor));
                if (page.Events.Count > 0 || DateTimeOffset.UtcNow >= deadline)
                {
                    return new EllaiSessionEventPageDto(
                        page.Events.Select(e => new EllaiSessionEventDto(
                            e.Sequence, e.SessionId, e.Kind, e.Ref, e.Detail, e.AtUtc)).ToList(),
                        page.Cursor);
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<EllaiGuidanceProgressDto?> ControlGuidanceAsync(
        string sessionId, EllaiGuidanceControlRequest request, CancellationToken cancellationToken)
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = ResolvePrincipal(state.User, sessionId);
        if (principal is null) return null;
        var actor = ActorContext.FromValidatedPrincipal(principal);
        await using var scope = _serviceProvider.CreateAsyncScope();
        return await EllaiGuidanceProposalEndpoint.ControlOwnedAsync(actor, sessionId, request,
            scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EllaiGuidanceProgressDto?> CompleteGuidanceReceiptAsync(
        string sessionId, EllaiGuidanceReceiptRequest request, CancellationToken cancellationToken)
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = ResolvePrincipal(state.User, sessionId);
        if (principal is null) return null;
        var actor = ActorContext.FromValidatedPrincipal(principal);
        await using var scope = _serviceProvider.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<IEllaiSessionStore>().GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != actor.ActorId) return null;
        var authority = await scope.ServiceProvider.GetRequiredService<EllaiUiAuthorityResolver>()
            .ResolveAsync(actor, session.ScopeEpoch, cancellationToken).ConfigureAwait(false);
        return await _serviceProvider.GetRequiredService<SwiftAid.Gateway.Intake.WebGuestIntakeService>()
            .CompleteGuidanceReceiptAsync(sessionId, request, authority,
                scope.ServiceProvider.GetRequiredService<EllaiGuidanceProposalAdmission>(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<EllaiGuidanceProgressDto?> CompleteGuidanceStepAsync(
        string sessionId, EllaiGuidanceStepRequest request, CancellationToken cancellationToken)
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = ResolvePrincipal(state.User, sessionId);
        if (principal is null) return null;
        var actor = ActorContext.FromValidatedPrincipal(principal);
        await using var scope = _serviceProvider.CreateAsyncScope();
        return await EllaiGuidanceProposalEndpoint.CompleteStepOwnedAsync(actor, sessionId, request,
            scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EllaiSessionResponseDto?> PrepareGuidanceNarrationAsync(
        string sessionId, EllaiGuidanceNarrationRequest request, CancellationToken cancellationToken)
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = ResolvePrincipal(state.User, sessionId);
        if (principal is null) return null;
        await using var scope = _serviceProvider.CreateAsyncScope();
        return await EllaiGuidanceProposalEndpoint.NarrateOwnedAsync(ActorContext.FromValidatedPrincipal(principal),
            sessionId, request, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EllaiGuidancePlanDto?> PrepareGuidancePlanAsync(
        string sessionId, string proposalId, CancellationToken cancellationToken)
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = ResolvePrincipal(state.User, sessionId);
        if (principal is null) return null;
        var actor = ActorContext.FromValidatedPrincipal(principal);
        await using var scope = _serviceProvider.CreateAsyncScope();
        return await EllaiGuidanceProposalEndpoint.PrepareOwnedAsync(actor, sessionId, proposalId,
            scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EllaiGuidancePlanDto?> RevalidateGuidancePlanAsync(
        string sessionId, string proposalId, string planId, string stepId, CancellationToken cancellationToken)
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = ResolvePrincipal(state.User, sessionId);
        if (principal is null) return null;
        await using var scope = _serviceProvider.CreateAsyncScope();
        return await EllaiGuidanceProposalEndpoint.PrepareOwnedAsync(ActorContext.FromValidatedPrincipal(principal),
            sessionId, proposalId, scope.ServiceProvider, cancellationToken, planId, stepId).ConfigureAwait(false);
    }

    public async Task<EllaiGuidanceAdmissionDto?> ReadGuidanceAdmissionAsync(
        string sessionId, string proposalId, CancellationToken cancellationToken)
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = ResolvePrincipal(state.User, sessionId);
        if (principal is null) return null;
        var actor = ActorContext.FromValidatedPrincipal(principal);
        await using var scope = _serviceProvider.CreateAsyncScope();
        return await EllaiGuidanceProposalEndpoint.ReadOwnedAsync(actor, sessionId, proposalId,
            scope.ServiceProvider.GetRequiredService<IEllaiSessionStore>(),
            scope.ServiceProvider.GetRequiredService<EllaiGuidanceProposalAdmission>(), cancellationToken,
            scope.ServiceProvider.GetService<EllaiUiAuthorityResolver>()).ConfigureAwait(false);
    }

    public async Task<EllaiSessionResponsePageDto?> PollResponsesAsync(
        string sessionId, long cursor, CancellationToken cancellationToken)
    {
        if (!await OwnsSessionAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var store = _serviceProvider.GetService<EllaiSessionResponseStore>();
        if (store is null) return null;
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = ResolvePrincipal(state.User, sessionId);
        if (principal is null) return null;
        var actor = ActorContext.FromValidatedPrincipal(principal);
        await using var responseScope = _serviceProvider.CreateAsyncScope();
        var outbox = responseScope.ServiceProvider.GetRequiredService<IEllaiUtteranceOutboxStore>();
        var conversation = await outbox.ReadConversationAsync(sessionId, actor.ActorId, cancellationToken).ConfigureAwait(false);
        var current = await responseScope.ServiceProvider.GetRequiredService<IEllaiSessionStore>()
            .GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.OwnerActorId != actor.ActorId) return null;
        var page = current.Status == "Cancelled"
            ? new EllaiSessionResponsePageDto([], Math.Max(0, cursor))
            : store.ReadSince(sessionId, Math.Max(0, cursor));
        return page with
        {
            StreamId = store.StreamId,
            Responses = page.Responses.Where(response => store.CanDeliver(sessionId, response, current.ScopeEpoch,
                current.InteractionEpoch, conversation, DateTimeOffset.UtcNow)).ToArray(),
            Conversation = conversation.Select(EllaiConversationProjection.ToClient).ToArray(),
        };
    }

    public async Task<bool> InterruptResponseAudioAsync(
        string sessionId,
        long sequence,
        string playbackId,
        CancellationToken cancellationToken,
        string? turnId = null,
        long streamEpoch = 0)
    {
        if (sequence < 1 || !Guid.TryParseExact(playbackId, "N", out var parsedPlaybackId)
            || !await OwnsSessionAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var responses = _serviceProvider.GetService<EllaiSessionResponseStore>();
        var response = responses?.ReadSince(sessionId, sequence - 1, take: 1)
            .Responses.FirstOrDefault(item => item.Sequence == sequence);
        if (response is null)
        {
            return false;
        }

        var turnCoordinator = _serviceProvider.GetService<EllaiVoiceTurnCoordinator>();
        var clientConfirmed = false;
        if (!string.IsNullOrWhiteSpace(turnId) || streamEpoch > 0)
        {
            if (!Guid.TryParse(turnId, out var parsedTurnId) || streamEpoch < 1
                || turnCoordinator is null)
            {
                return false;
            }

            clientConfirmed = turnCoordinator.ConfirmClientBargeIn(
                sessionId, streamEpoch, sequence, parsedPlaybackId, parsedTurnId);
            if (!clientConfirmed)
            {
                return false;
            }
        }

        // Local VAD only registers a candidate identity. Silero confirms it
        // from the continuing microphone stream before response generation is
        // cancelled. Calls without client-turn identity are explicit stops.
        var interrupted = false;
        if (!clientConfirmed)
        {
            turnCoordinator?.InterruptPlayback(sessionId, sequence, parsedPlaybackId);
            interrupted = _serviceProvider.GetService<EllaiResponseSpeechCoordinator>()
                ?.Interrupt(sessionId, sequence, parsedPlaybackId) == true;
        }
        return interrupted || clientConfirmed;
    }

    public async Task<EllaiReviewDto?> GetActiveReviewAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!await OwnsSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            || !await HasCurrentScopedStaffAuthorizationAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return await WithAuthenticatedAsync<EllaiReviewDto?>(dialogs =>
        {
            var active = dialogs.TryGetActive(sessionId, DateTimeOffset.UtcNow);
            return Task.FromResult(active is null ? null : MapReview(active));
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ApproveSelectedAsync(
        string sessionId, string requestId, IReadOnlyList<string> actionIds,
        CancellationToken cancellationToken)
    {
        if (!await OwnsSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            || !await HasCurrentScopedStaffAuthorizationAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var dialogs = _serviceProvider.GetService<EllaiApprovalDialogCoordinator>();
            var approvals = _serviceProvider.GetService<EllaiApprovalService>();
            var journals = _serviceProvider.GetService<EllaiSessionEventJournal>();
            if (dialogs is null || approvals is null || journals is null
                || actionIds is null || actionIds.Count == 0
                || actionIds.Count > EllaiDecisionValidator.MaxPlanActions)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var review = dialogs.Get(requestId);
            if (review is null
                || !string.Equals(review.SessionId, sessionId, StringComparison.Ordinal)
                || review.State != EllaiApprovalDialogState.AwaitingApproval)
            {
                return null;
            }

            var receipts = await approvals.ApproveReviewedAsync(
                review, actionIds, now, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (receipts is null)
            {
                return null;
            }

            dialogs.Resolve(requestId, approved: true, now);
            journals.Append(sessionId, "approval-resolved", requestId, "Approved", now);
            return receipts.Select(receipt => new EllaiApprovedReceiptDto(
                receipt.ReceiptId, receipt.ActionId, receipt.Status.ToString())).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> RejectReviewAsync(
        string sessionId, string requestId, CancellationToken cancellationToken)
    {
        if (!await OwnsSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            || !await HasCurrentScopedStaffAuthorizationAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            var dialogs = _serviceProvider.GetService<EllaiApprovalDialogCoordinator>();
            var journals = _serviceProvider.GetService<EllaiSessionEventJournal>();
            if (dialogs is null || journals is null)
            {
                return false;
            }

            var review = dialogs.Get(requestId);
            if (review is null
                || !string.Equals(review.SessionId, sessionId, StringComparison.Ordinal)
                || review.State != EllaiApprovalDialogState.AwaitingApproval)
            {
                return false;
            }

            dialogs.Resolve(requestId, approved: false, DateTimeOffset.UtcNow);
            journals.Append(sessionId, "approval-resolved", requestId, "Rejected", DateTimeOffset.UtcNow);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ListReceiptsAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        if (!await OwnsSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            || !await HasCurrentScopedStaffAuthorizationAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var receipts = _serviceProvider.GetService<IEllaiApprovalStore>();
            if (receipts is null)
            {
                return null;
            }

            var list = await receipts.ListBySessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return list.Select(receipt => new EllaiApprovedReceiptDto(
                receipt.ReceiptId, receipt.ActionId, receipt.Status.ToString())).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> ReportUiObservationAsync(
        string sessionId, UiObservationReport report, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || report is null)
        {
            return false;
        }

        if (!await OwnsSessionAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            var currentState = await _authenticationStateProvider
                .GetAuthenticationStateAsync().ConfigureAwait(false);
            var currentPrincipal = ResolvePrincipal(currentState.User, sessionId);
            if (currentPrincipal is null)
            {
                return false;
            }

            var observations = _serviceProvider.GetService<IEllaiUiObservationStore>();
            if (observations is null
                || !report.IsValid())
            {
                return false;
            }

            return await observations.RecordAsync(new StoredUiObservationReport(
                sessionId, report.ViewInstanceId.Trim(), report.ContextStamp.Trim(),
                report.Platform.Trim(), report.MountedControlIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .ToList(),
                DateTimeOffset.UtcNow, report.ControlStates, report.CurrentNavigationId, report.CompletedControlIds, report.ValidationInputIds), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static EllaiReviewDto MapReview(EllaiApprovalDialogRequest review) => new(
        review.RequestId, review.SessionId, review.PlanHash, review.PlanVersion,
        review.State.ToString(), review.CreatedAtUtc, review.ExpiresAtUtc, review.Actions);

    private async Task<bool> IsAuthenticatedAsync()
    {
        try
        {
            var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
            return state.User?.Identity?.IsAuthenticated == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> OwnsSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        try
        {
            var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
            var principal = ResolvePrincipal(state.User, sessionId);
            if (principal is null) return false;
            var actor = SwiftAid.Application.Authorization.ActorContext.FromValidatedPrincipal(principal);
            await using var ownershipScope = _serviceProvider.CreateAsyncScope();
            var store = ownershipScope.ServiceProvider.GetService<IEllaiSessionStore>();
            if (store is null) return false;
            var record = await store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return record is not null
                && !string.IsNullOrWhiteSpace(record.OwnerActorId)
                && string.Equals(record.OwnerActorId, actor.ActorId, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; }
    }

    private ClaimsPrincipal? ResolvePrincipal(ClaimsPrincipal? currentPrincipal, string sessionId)
    {
        if (currentPrincipal?.Identity?.IsAuthenticated == true)
        {
            return currentPrincipal;
        }

        // This resolves identity only; every session operation still verifies its persisted owner.
        return _guestSessionIdentity.TryGetHistoryPrincipal(out var guestPrincipal)
            ? guestPrincipal
            : null;
    }

    private static Task<bool> HasCurrentScopedStaffAuthorizationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // SwiftAidStaffApi currently establishes authentication only. Until a
        // trusted server-issued staff + active-scope capability is available,
        // privileged review/evidence operations fail closed.
        return Task.FromResult(false);
    }

    private async Task<T?> WithAuthenticatedAsync<T>(
        Func<EllaiApprovalDialogCoordinator, Task<T?>> action)
    {
        if (!await IsAuthenticatedAsync().ConfigureAwait(false))
        {
            return default;
        }

        try
        {
            var dialogs = _serviceProvider.GetService<EllaiApprovalDialogCoordinator>();
            if (dialogs is null)
            {
                return default;
            }

            return await action(dialogs).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return default;
        }
    }
}
