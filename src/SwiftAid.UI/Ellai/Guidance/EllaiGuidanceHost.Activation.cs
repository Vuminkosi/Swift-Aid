using Microsoft.AspNetCore.Components;
using Bit.Bonboarding;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

public partial class EllaiGuidanceHost
{
    private string? _guidanceProposalId;
    private bool _guidanceProposalIsAccepted;
    private string? _guidanceFeatureId;
    private string? _guidanceProposalTurn;
    private string? _pendingGuidanceTurn;
    private string? _lastGuidanceControlProposalId;
    private string? _lastTabSelectionProposalId;
    private string? _lastCaseNavigationProposalId;
    private string? _lastCaseFilterProposalId;
    private string? _lastFeatureNavigationProposalId;
    private readonly EllaiNavigationTrail _navigationTrail = new();
    private string? _guidanceNotice;
    private string? _validationGuidanceText;
    private bool _preparingGuidance;
    private string? _queuedGuidanceProposalId;
    private IBonboardingTourSession? _generatedTour;
    private ValidatedGuidancePlan? _generatedPlan;
    private bool _advancingValidatedInput;
    private EventHandler<BonboardingTourSessionChangedEventArgs>? _generatedTourChanged;
    private Func<string, Task<bool>>? _controlGeneratedTour;
    private Func<string, Task>? _narrateValidation;

    private void OnValidationGuidanceChanged(string? message)
    {
        var tour = _generatedTour;
        if (tour?.Status is not (BonboardingTourStatus.Running or BonboardingTourStatus.Waiting)) return;
        _ = InvokeAsync(() =>
        {
            if (!ReferenceEquals(tour, _generatedTour) ||
                tour.Status is not (BonboardingTourStatus.Running or BonboardingTourStatus.Waiting)) return;
            var changed = !string.Equals(_validationGuidanceText, message, StringComparison.Ordinal);
            if (!changed) return;
            if (changed) CancelValidationNarration();
            if (!string.IsNullOrWhiteSpace(message))
            {
                CancelGuidanceCountdown();
                StopResponseAudio();
                _guidanceNarrationEventId = null;
                _latestResponseSequence = 0;
                _latestResponseAudioSequences.Clear();
                _pendingResponseAudioSequences.Clear();
                _validationGuidanceText = message;
                _latestResponseText = message;
                if (changed && _readResponsesAloud && _narrateValidation is not null)
                    _ = _narrateValidation(message);
            }
            else
            {
                StopResponseAudio();
                _latestResponseSequence = 0;
                _latestResponseAudioSequences.Clear();
                _pendingResponseAudioSequences.Clear();
                if (_latestResponseText == _validationGuidanceText) _latestResponseText = null;
                _validationGuidanceText = null;
            }
            _guidanceNotice = message;
            StateHasChanged();
        });
    }

    private async Task AdvanceValidatedInputAsync(CancellationToken ct)
    {
        if (_guidanceCountdownSeconds is not null || _guidanceNarrationCts is not null || _isPlayingResponse) return;
        var tour = _generatedTour;
        if (_advancingValidatedInput || tour is null || _generatedPlan is null ||
            tour.Status is not (BonboardingTourStatus.Running or BonboardingTourStatus.Waiting)) return;
        var step = _generatedPlan.Steps.SingleOrDefault(item => item.StepKey == tour.CurrentStep?.Id);
        if (step?.ControlId is "report.submit" or "report.receipt" &&
            Services.GetService(typeof(EllaiDisplayedReceiptContext)) is EllaiDisplayedReceiptContext { Current: not null })
        {
            await AdvanceGuidanceStepAsync(ct);
            return;
        }
        if (step?.Behavior != PlanStepBehavior.GuideHuman ||
            !EllaiReportUiManifest.All.Any(control => control.ControlId == step.ControlId &&
                        (control.ProofPolicyId == "proof-ui-validated" || control.ControlId == "report.confirmations")) ||
            _latestGuidanceObservation?.CompletedControlIds?.Contains(step.ControlId) != true) return;
        await AdvanceGuidanceStepAsync(ct);
    }

    private async Task AdvanceGuidanceStepAsync(CancellationToken ct)
    {
        var tour = _generatedTour;
        if (_advancingValidatedInput || tour is null ||
            tour.Status is not (BonboardingTourStatus.Running or BonboardingTourStatus.Waiting)) return;
        _advancingValidatedInput = true;
        try
        {
            // Bonboarding invokes CanAdvance, which reobserves and commits the existing server proof boundary.
            await tour.NextAsync(ct);
        }
        finally { _advancingValidatedInput = false; }
    }

    private async Task MoveGuidanceStepAsync(string command)
    {
        CancelGuidanceCountdown();
        if (_advancingValidatedInput || _generatedTour?.Status is not (BonboardingTourStatus.Running or BonboardingTourStatus.Waiting)) return;
        _advancingValidatedInput = true;
        try { await ControlGeneratedTourAsync(command); }
        finally { _advancingValidatedInput = false; }
    }

    private async Task ControlGeneratedTourAsync(string command)
    {
        CancelGuidanceCountdown();
        var tour = _generatedTour;
        if (tour is null || _controlGeneratedTour is null) return;
        if (!await _controlGeneratedTour(command)) return;
        if (!ReferenceEquals(tour, _generatedTour)) return;
        if (command is "back" or "skip")
        {
            await CancelGeneratedGuidanceAsync();
            await StartProposedGuidanceAsync();
        }
        else if (command == "pause") await tour.PauseAsync(CancellationToken.None);
        else if (command == "resume") await tour.ResumeAsync(CancellationToken.None);
        else await tour.CancelAsync(CancellationToken.None);
    }

    private void ObserveGuidanceProposal(EllaiConversationTurnDto turn, bool newlyCompleted)
    {
        if (_guidanceProposalTurn != turn.ClientUtteranceId && _generatedTour is null && !_guidanceProposalIsAccepted)
        {
            _guidanceProposalId = null;
            _guidanceFeatureId = null;
            _guidanceProposalTurn = turn.ClientUtteranceId;
            _guidanceNotice = null;
            _ = CancelGeneratedGuidanceAsync();
        }
        if (turn.Outcome is "Queued" or "Processing") _pendingGuidanceTurn = turn.ClientUtteranceId;
        if (turn.Outcome != "Completed") return;
        newlyCompleted |= _pendingGuidanceTurn == turn.ClientUtteranceId;
        _pendingGuidanceTurn = null;
        var navigationProposals = turn.UiProposals.Where(item => item.Proposal.Kind == UiIntentKind.NavigateFeature).Take(2).ToArray();
        if (navigationProposals.Length > 0)
        {
            if (navigationProposals.Length == 1 && newlyCompleted && navigationProposals[0].ProposalId != _lastFeatureNavigationProposalId &&
                EllaiFeatureNavigationProposal.IsValid(navigationProposals[0].Proposal))
            {
                _lastFeatureNavigationProposalId = navigationProposals[0].ProposalId;
                _ = NavigateFeatureAsync(turn.ClientUtteranceId, navigationProposals[0].ProposalId, navigationProposals[0].Proposal);
            }
            return;
        }
        var filterProposals = turn.UiProposals.Where(item => item.Proposal.Kind == UiIntentKind.SetPermittedFilter).Take(2).ToArray();
        if (filterProposals.Length > 0)
        {
            if (filterProposals.Length == 1 && newlyCompleted && filterProposals[0].ProposalId != _lastCaseFilterProposalId &&
                EllaiCaseFilterProposal.IsValid(filterProposals[0].Proposal))
            {
                _lastCaseFilterProposalId = filterProposals[0].ProposalId;
                _ = ApplyCaseFilterAsync(turn.ClientUtteranceId, filterProposals[0].ProposalId, filterProposals[0].Proposal);
            }
            return;
        }
        var recordProposals = turn.UiProposals.Where(item => item.Proposal.Kind == UiIntentKind.OpenAuthorizedRecord).Take(2).ToArray();
        if (recordProposals.Length > 0)
        {
            if (recordProposals.Length == 1 && newlyCompleted && recordProposals[0].ProposalId != _lastCaseNavigationProposalId &&
                EllaiCaseNavigationProposal.ReadCaseId(recordProposals[0].Proposal) is not null)
            {
                _lastCaseNavigationProposalId = recordProposals[0].ProposalId;
                _ = OpenAuthorizedCaseAsync(turn.ClientUtteranceId, recordProposals[0].ProposalId, recordProposals[0].Proposal);
            }
            return;
        }
        var tabProposals = turn.UiProposals.Where(item => item.Proposal.Kind == UiIntentKind.SelectTab).Take(2).ToArray();
        if (tabProposals.Length > 0)
        {
            if (tabProposals.Length == 1 && newlyCompleted && tabProposals[0].ProposalId != _lastTabSelectionProposalId &&
                EllaiCaseDetailUiManifest.ReadTab(tabProposals[0].Proposal) is { } tab)
            {
                _lastTabSelectionProposalId = tabProposals[0].ProposalId;
                _ = SelectCurrentCaseTabAsync(turn.ClientUtteranceId, tabProposals[0].ProposalId, tab);
            }
            return;
        }
        var controls = turn.UiProposals.Where(p => p.Proposal.Kind == UiIntentKind.ControlGuidance).Take(2).ToArray();
        if (controls.Length > 1) return;
        var control = controls.SingleOrDefault();
        if (control is not null)
        {
            if (newlyCompleted && control.ProposalId != _lastGuidanceControlProposalId &&
                EllaiGuidanceControlProposal.ReadCommand(control.Proposal) is { } command)
            {
                _lastGuidanceControlProposalId = control.ProposalId;
                if (command == "stop-speech") DisableResponseAudio();
                else _ = DispatchConversationalGuidanceControlAsync(command);
            }
            return;
        }
        var proposal = turn.UiProposals.FirstOrDefault(p => p.Proposal.Kind == UiIntentKind.StartGuidance);
        if (proposal is null || _guidanceProposalId == proposal.ProposalId) return;
        _ = CancelGeneratedGuidanceAsync();
        _guidanceProposalTurn = turn.ClientUtteranceId;
        _guidanceProposalId = proposal.ProposalId;
        _guidanceProposalIsAccepted = false;
        _guidanceFeatureId = proposal.Proposal.Arguments.GetValueOrDefault("feature");
        // Conversation restoration offers an explicit action; only the currently awaited turn auto-starts.
        if (newlyCompleted) _ = StartProposedGuidanceAsync();
    }

    private void TrackNavigation(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs args)
    {
        try { _navigationTrail.Record(Navigation.ToBaseRelativePath(args.Location)); }
        catch (ArgumentException) { _navigationTrail.Record(string.Empty); }
    }

    private async Task NavigateFeatureAsync(string turnId, string proposalId, UiIntentProposal proposal)
    {
        if (_sessionView is not { Status: "Active" } session || !EllaiFeatureNavigationProposal.IsValid(proposal)) return;
        var originalUri = Navigation.Uri;
        var back = EllaiFeatureNavigationProposal.IsBack(proposal);
        var route = back ? _navigationTrail.Previous : EllaiFeatureNavigationProposal.Destination(proposal)?.Route;
        bool IsCurrent() => !_uiObservationLifetime.IsCancellationRequested && Navigation.Uri == originalUri &&
            _sessionView?.SessionId == session.SessionId && _sessionView.Status == "Active" &&
            _lastDurableTurn is { Outcome: "Completed" } current && current.ClientUtteranceId == turnId &&
            _lastFeatureNavigationProposalId == proposalId;
        try
        {
            if (!IsCurrent()) return;
            var auth = Services.GetService(typeof(Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider))
                as Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider;
            var signedIn = auth is not null && (await auth.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated == true;
            if (!IsCurrent()) return;
            if (!signedIn || route is null)
            {
                _guidanceNotice = back ? "No previous available page to return to." : "That page is not available with your current access.";
            }
            else if (route.StartsWith("/case/", StringComparison.Ordinal))
            {
                var client = Services.GetService(typeof(SwiftAid.Contracts.Cases.ICaseTimelineClient)) as SwiftAid.Contracts.Cases.ICaseTimelineClient;
                var target = new UiIntentProposal(UiIntentKind.OpenAuthorizedRecord, "case-detail", route[6..], null, new Dictionary<string, string>());
                var authorizedRoute = client is null ? null : await EllaiAuthorizedCaseNavigation.ResolveAsync(target, client, _uiObservationLifetime.Token);
                if (!IsCurrent()) return;
                if (authorizedRoute is null) _guidanceNotice = "That case could not be opened with your current access.";
                else Navigation.NavigateTo(authorizedRoute);
            }
            else
            {
                await ReportUiForFeatureAsync(EllaiNavigationManifest.FeatureId, _uiObservationLifetime.Token);
                if (!IsCurrent()) return;
                var destination = EllaiNavigationManifest.Destinations.Single(item => item.Route == route);
                // A collapsed menu may be hidden; disabled, absent or ambiguous targets cannot navigate.
                bool TargetAvailable() => _latestGuidanceObservation?.ControlStates?.Count(control => control.ControlId == destination.ControlId &&
                    control.State is UiObservationState.Ready or UiObservationState.Hidden) == 1;
                if (!TargetAvailable() && RevealNavigation.HasDelegate)
                {
                    await RevealNavigation.InvokeAsync();
                    for (var attempt = 0; attempt < 10 && IsCurrent() && !TargetAvailable(); attempt++)
                    {
                        await Task.Delay(100, _uiObservationLifetime.Token);
                        await ReportUiForFeatureAsync(EllaiNavigationManifest.FeatureId, _uiObservationLifetime.Token);
                    }
                }
                if (!IsCurrent()) return;
                var available = TargetAvailable();
                var stillSignedIn = auth is not null && (await auth.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated == true;
                if (!IsCurrent()) return;
                if (available && stillSignedIn) Navigation.NavigateTo(route);
                else _guidanceNotice = "That page is not available right now.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is HttpRequestException or Microsoft.JSInterop.JSException)
        {
            if (IsCurrent()) _guidanceNotice = "The page could not be opened. Try again.";
        }
        if (IsCurrent()) await InvokeAsync(StateHasChanged);
    }

    private async Task ApplyCaseFilterAsync(string turnId, string proposalId, UiIntentProposal proposal)
    {
        if (_sessionView is not { Status: "Active" } session || !EllaiCaseFilterProposal.IsValid(proposal)) return;
        var originalUri = Navigation.Uri;
        bool IsCurrent() => !_uiObservationLifetime.IsCancellationRequested && Navigation.Uri == originalUri &&
            _sessionView?.SessionId == session.SessionId && _sessionView.Status == "Active" &&
            _lastDurableTurn is { Outcome: "Completed" } current && current.ClientUtteranceId == turnId &&
            _lastCaseFilterProposalId == proposalId;
        try
        {
            if (!IsCurrent()) return;
            await ReportUiForFeatureAsync(EllaiCaseListUiManifest.FeatureId, _uiObservationLifetime.Token);
            if (!IsCurrent()) return;
            var ready = _latestGuidanceObservation?.ControlStates?.Count(control =>
                control.ControlId == proposal.ControlId && control.State == UiObservationState.Ready) == 1;
            var context = Services.GetService(typeof(EllaiCaseFilterContext)) as EllaiCaseFilterContext;
            var applied = ready && context is not null && await context.ApplyAsync(proposal, _uiObservationLifetime.Token);
            if (IsCurrent()) _guidanceNotice = applied ? "Filter requested." : "That filter is not available on this page.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is HttpRequestException or Microsoft.JSInterop.JSException)
        {
            if (IsCurrent()) _guidanceNotice = "The filter could not be applied. Try again.";
        }
        if (IsCurrent()) await InvokeAsync(StateHasChanged);
    }

    private async Task OpenAuthorizedCaseAsync(string turnId, string proposalId, UiIntentProposal proposal)
    {
        if (_sessionView is not { Status: "Active" } session) return;
        var originalUri = Navigation.Uri;
        bool IsCurrent() => !_uiObservationLifetime.IsCancellationRequested && Navigation.Uri == originalUri &&
            _sessionView?.SessionId == session.SessionId && _sessionView.Status == "Active" &&
            _lastDurableTurn is { Outcome: "Completed" } current && current.ClientUtteranceId == turnId &&
            _lastCaseNavigationProposalId == proposalId;
        try
        {
            if (!IsCurrent()) return;
            var client = Services.GetService(typeof(SwiftAid.Contracts.Cases.ICaseTimelineClient)) as SwiftAid.Contracts.Cases.ICaseTimelineClient;
            var route = client is null ? null : await EllaiAuthorizedCaseNavigation.ResolveAsync(proposal, client, _uiObservationLifetime.Token);
            if (!IsCurrent()) return;
            if (route is null) _guidanceNotice = "That case could not be opened with your current access.";
            else Navigation.NavigateTo(route);
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException)
        {
            if (IsCurrent()) _guidanceNotice = "The case could not be opened. Try again.";
        }
        if (IsCurrent()) await InvokeAsync(StateHasChanged);
    }

    private async Task SelectCurrentCaseTabAsync(string turnId, string proposalId, string tab)
    {
        if (!EllaiCaseDetailUiManifest.Tabs.Contains(tab) || _sessionView is not { Status: "Active" } session) return;
        var originalUri = Navigation.Uri;
        var path = Navigation.ToBaseRelativePath(originalUri).Split('?', '#')[0].TrimEnd('/');
        var segments = path.Split('/');
        if (segments.Length != 2 || segments[0] != "case" || !Guid.TryParseExact(segments[1], "D", out var caseId) || caseId == Guid.Empty) return;
        bool IsCurrent() => !_uiObservationLifetime.IsCancellationRequested && Navigation.Uri == originalUri &&
            _sessionView?.SessionId == session.SessionId && _sessionView.Status == "Active" &&
            _lastDurableTurn is { Outcome: "Completed" } current && current.ClientUtteranceId == turnId &&
            _lastTabSelectionProposalId == proposalId;
        try
        {
            if (!IsCurrent()) return;
            await ReportUiForFeatureAsync(EllaiCaseDetailUiManifest.FeatureId, _uiObservationLifetime.Token);
            if (!IsCurrent() || _latestGuidanceObservation?.ControlStates?.Count(state =>
                state.ControlId == "case-detail.tabs" && state.State == UiObservationState.Ready) != 1) return;
            // Only the existing component's registered tab query changes. Record identity
            // and all application data/action authorization remain with the current page.
            Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("tab", tab));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is HttpRequestException or Microsoft.JSInterop.JSException)
        {
            _guidanceNotice = "The tab could not be opened. Try again.";
        }
    }

    private void OfferSavedGuidance(EllaiSessionView session)
    {
        if (session.Status != "Active" || session.Guidance is not { Status: "Ready" or "Paused" } saved || _generatedTour is not null) return;
        _guidanceProposalId = saved.ProposalId;
        _guidanceFeatureId = saved.FeatureId;
        _guidanceProposalTurn = _lastDurableTurn?.ClientUtteranceId;
        _guidanceProposalIsAccepted = true;
    }

    private async Task DispatchConversationalGuidanceControlAsync(string command)
    {
        try
        {
            if (_generatedTour is null)
            {
                if (_sessionView is not { Status: "Active" } session) return;
                var controlId = _lastGuidanceControlProposalId;
                var fresh = await ReviewClient.ResumeSessionAsync(session.SessionId, _uiObservationLifetime.Token);
                if (_sessionView?.SessionId != session.SessionId || controlId != _lastGuidanceControlProposalId) return;
                if (fresh.Status != EllaiClientOperationStatus.Accepted || fresh.Session?.SessionId != session.SessionId ||
                    fresh.Session.Status != "Active" || fresh.Session.Guidance is not { } saved)
                {
                    _guidanceNotice = "There is no saved guidance to continue.";
                    return;
                }
                OfferSavedGuidance(fresh.Session);
                if (command is "resume" or "next" or "back" or "skip")
                {
                    if (command is "next" or "back" or "skip" && saved.Status != "Ready")
                    {
                        _guidanceNotice = "Resume the guidance before changing steps.";
                        return;
                    }
                    await StartProposedGuidanceAsync();
                    if (command == "next" && controlId == _lastGuidanceControlProposalId &&
                        _sessionView?.SessionId == session.SessionId)
                        await AdvanceGuidanceStepAsync(_uiObservationLifetime.Token);
                    else if (command is "back" or "skip" && controlId == _lastGuidanceControlProposalId &&
                        _sessionView?.SessionId == session.SessionId)
                        await MoveGuidanceStepAsync(command);
                }
                else
                {
                    var progress = await ReviewClient.ControlGuidanceAsync(session.SessionId,
                        new(saved.ProposalId, saved.PlanId, saved.Version, command), _uiObservationLifetime.Token);
                    if (_sessionView?.SessionId != session.SessionId || controlId != _lastGuidanceControlProposalId) return;
                    if (progress?.Status == "Cancelled")
                    {
                        _guidanceProposalId = null;
                        _guidanceFeatureId = null;
                        _guidanceProposalIsAccepted = false;
                    }
                    else if (progress?.Status != "Paused") _guidanceNotice = "Guidance could not be updated. Try again.";
                }
                return;
            }
            if (command == "next") await AdvanceGuidanceStepAsync(_uiObservationLifetime.Token);
            else if (command is "back" or "skip") await MoveGuidanceStepAsync(command);
            else await ControlGeneratedTourAsync(command);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is HttpRequestException or Microsoft.JSInterop.JSException)
        {
            _guidanceNotice = "Guidance could not be updated. Try again.";
        }
        finally { if (!_uiObservationLifetime.IsCancellationRequested) await InvokeAsync(StateHasChanged); }
    }

    private async Task CancelGeneratedGuidanceAsync()
    {
        EndGuidanceNarration();
        EndGuidanceSpeech();
        var tour = _generatedTour;
        _generatedTour = null;
        _generatedPlan = null;
        _controlGeneratedTour = null;
        _narrateValidation = null;
        if (tour is null) return;
        if (_generatedTourChanged is not null) tour.Changed -= _generatedTourChanged;
        _generatedTourChanged = null;
        try { await tour.CancelAsync(CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Microsoft.JSInterop.JSDisconnectedException) { }
    }

    private async Task StartProposedGuidanceAsync()
    {
        if (_preparingGuidance)
        {
            // Preserve the latest explicit start while the prior provider request settles.
            // Never replay an obsolete proposal or start concurrent planning requests.
            _queuedGuidanceProposalId = _guidanceProposalId;
            return;
        }
        if (_sessionView is not { Status: "Active" } session ||
            _guidanceProposalId is not { } proposalId || _generatedTour is not null) return;
        _preparingGuidance = true;
        _guidanceNotice = null;
        if (_guidanceProposalIsAccepted) _guidanceProposalTurn = _lastDurableTurn?.ClientUtteranceId;
        var turnId = _guidanceProposalTurn;
        var activated = false;
        bool IsCurrent() => !_uiObservationLifetime.IsCancellationRequested &&
            _sessionView?.SessionId == session.SessionId && _sessionView.Status == "Active" &&
            _guidanceProposalId == proposalId && _guidanceProposalTurn == turnId &&
            (activated || _lastDurableTurn is null || _lastDurableTurn.ClientUtteranceId == turnId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_uiObservationLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(100));
        var token = timeout.Token;
        try
        {
            await ReportCurrentUiAsync(token);
            if (!IsCurrent()) return;
            if (_guidanceFeatureId == EllaiNavigationManifest.FeatureId && RevealNavigation.HasDelegate)
            {
                // A durable server-admitted proposal may reveal presentation, never grant access.
                var admission = await ReviewClient.ReadGuidanceAdmissionAsync(session.SessionId, proposalId, token);
                if (!IsCurrent()) return;
                if (admission?.Status is "Ready" or "NeedsObservation" or "NeedsNavigation" or "WaitingForControl")
                {
                    await RevealNavigation.InvokeAsync();
                    for (var attempt = 0; attempt < 40 && IsCurrent(); attempt++)
                    {
                        await Task.Delay(250, token);
                        await ReportCurrentUiAsync(token);
                        if (EllaiNavigationManifest.Controls.All(control =>
                            _latestGuidanceObservation?.ControlStates?.Any(state =>
                                state.ControlId == control.ControlId && state.State == UiObservationState.Ready) == true)) break;
                    }
                }
            }
            if (!IsCurrent()) return;
            var result = await ReviewClient.PrepareGuidancePlanAsync(session.SessionId, proposalId, token);
            if (!IsCurrent()) return;
            var destination = result?.NavigationId == EllaiReportUiManifest.NavigationId ? "/report" :
                _guidanceFeatureId == EllaiDashboardUiManifest.FeatureId && result?.NavigationId == EllaiDashboardUiManifest.NavigationId
                    ? EllaiNavigationManifest.ResolveRoute(EllaiDashboardUiManifest.NavigationId) :
                _guidanceFeatureId == EllaiCaseListUiManifest.FeatureId && result?.NavigationId == EllaiCaseListUiManifest.NavigationId
                    ? EllaiNavigationManifest.ResolveRoute(EllaiCaseListUiManifest.NavigationId) :
                _guidanceFeatureId == EllaiCommunityUiManifest.FeatureId && result?.NavigationId == EllaiCommunityUiManifest.NavigationId
                    ? EllaiNavigationManifest.ResolveRoute(EllaiCommunityUiManifest.NavigationId) :
                _guidanceFeatureId == EllaiSurveillanceUiManifest.FeatureId && result?.NavigationId == EllaiSurveillanceUiManifest.NavigationId
                    ? EllaiNavigationManifest.ResolveRoute(EllaiSurveillanceUiManifest.NavigationId) :
                _guidanceFeatureId == EllaiInvestigationUiManifest.FeatureId && result?.NavigationId == EllaiInvestigationUiManifest.NavigationId
                    ? EllaiNavigationManifest.ResolveRoute(EllaiInvestigationUiManifest.NavigationId) :
                _guidanceFeatureId == EllaiMonitoringUiManifest.FeatureId && result?.NavigationId == EllaiMonitoringUiManifest.NavigationId
                    ? EllaiNavigationManifest.ResolveRoute(EllaiMonitoringUiManifest.NavigationId) :
                _guidanceFeatureId == EllaiOrganizationUiManifest.FeatureId && result?.NavigationId == EllaiOrganizationUiManifest.NavigationId
                    ? EllaiNavigationManifest.ResolveRoute(EllaiOrganizationUiManifest.NavigationId) : null;
            if (result?.Status == "NeedsNavigation" && destination is not null)
            {
                // This is a registered destination, never a model-supplied URL.
                Navigation.NavigateTo(destination);
                for (var attempt = 0; attempt < 40 && IsCurrent(); attempt++)
                {
                    await Task.Delay(250, token);
                    await ReportCurrentUiAsync(token);
                    var entry = _guidanceFeatureId switch
                    {
                        EllaiDashboardUiManifest.FeatureId => "dashboard.overview",
                        EllaiCaseListUiManifest.FeatureId => "case-list.overview",
                        EllaiCommunityUiManifest.FeatureId => "community-sessions.browse",
                        EllaiSurveillanceUiManifest.FeatureId => "surveillance-map.overview",
                        EllaiInvestigationUiManifest.FeatureId => "investigations.overview",
                        EllaiMonitoringUiManifest.FeatureId => "monitoring.assigned.overview",
                        EllaiOrganizationUiManifest.FeatureId => "organizations.overview",
                        _ => "report.entry",
                    };
                    if (_latestGuidanceObservation?.MountedControlIds.Contains(entry) == true) break;
                }
                if (!IsCurrent()) return;
                result = await ReviewClient.PrepareGuidancePlanAsync(session.SessionId, proposalId, token);
            }
            if (!IsCurrent()) return;
            if (result?.Status is "Cancelled" or "Completed")
            {
                _guidanceProposalId = null;
                _guidanceProposalIsAccepted = false;
                _guidanceFeatureId = null;
                _guidanceNotice = null;
                return;
            }
            await ReportCurrentUiAsync(token);
            if (!IsCurrent() || result?.Plan is not { } plan || result.Status is not ("Ready" or "Paused") ||
                _latestGuidanceObservation?.ContextStamp != result.ContextStamp)
            {
                _guidanceNotice = "Guidance is not ready yet. Try again when the page has loaded.";
                return;
            }
            var anchors = EllaiReportUiManifest.All.Concat(EllaiNavigationManifest.Controls).Concat(EllaiDashboardUiManifest.Controls).Concat(EllaiCaseListUiManifest.Controls).Concat(EllaiCaseDetailUiManifest.Controls).Concat(EllaiCommunityUiManifest.Controls).Concat(EllaiSurveillanceUiManifest.Controls).Concat(EllaiInvestigationUiManifest.Controls).Concat(EllaiMonitoringUiManifest.Controls).Concat(EllaiOrganizationUiManifest.Controls)
                .ToDictionary(c => c.ControlId, c => c.AnchorBindingKey);
            if (plan.Steps.Any(step => step.ControlId is null || !anchors.ContainsKey(step.ControlId)))
            {
                _guidanceNotice = "This guidance is not available on the current page.";
                return;
            }
            _guidanceProposalIsAccepted = true;
            var progressVersion = result.ProgressVersion;
            var durableStatus = result.Status;
            var progressGate = new SemaphoreSlim(1, 1);
            async Task<bool> PersistControl(string command)
            {
                await progressGate.WaitAsync(_uiObservationLifetime.Token);
                try
                {
                    if (!IsCurrent()) return false;
                    if (command is "resume" or "back" or "skip") await ReportCurrentUiAsync(_uiObservationLifetime.Token);
                    var target = command switch { "pause" => "Paused", "resume" or "back" or "skip" => "Ready", _ => "Cancelled" };
                    if (command is not ("back" or "skip") && durableStatus == target) return true;
                    var update = await ReviewClient.ControlGuidanceAsync(session.SessionId,
                        new(proposalId, plan.PlanId, progressVersion, command), _uiObservationLifetime.Token);
                    if (IsCurrent() && command == "back" && update?.Status == "AtStart")
                    {
                        _guidanceNotice = "You are already at the first step.";
                        return false;
                    }
                    if (IsCurrent() && command == "skip" && update?.Status == "SkipNotAllowed")
                    {
                        _guidanceNotice = "This step needs to be completed before continuing.";
                        return false;
                    }
                    if (!IsCurrent() || (update?.Status != target && !(command == "skip" && update?.Status == "Completed")))
                    {
                        _guidanceNotice = "Guidance could not be saved. Try again.";
                        return false;
                    }
                    progressVersion = update.Version;
                    durableStatus = update.Status;
                    _guidanceNotice = null;
                    if (command == "skip" && update.Status == "Completed")
                    {
                        _guidanceProposalId = null;
                        _guidanceFeatureId = null;
                        _guidanceProposalIsAccepted = false;
                        _guidanceNotice = "Guidance finished. Skipped steps were not completed.";
                    }
                    return true;
                }
                finally { progressGate.Release(); }
            }
            if (result.Status == "Paused" && !await PersistControl("resume")) return;
            var definition = new EllaiBonboardingCompiler().Compile(plan, anchors, async (stepId, ct) =>
            {
                if (!IsCurrent()) return false;
                await ReportCurrentUiAsync(ct);
                var admission = await ReviewClient.ReadGuidanceAdmissionAsync(session.SessionId, proposalId, ct);
                if (admission?.Status != "Ready") return false;
                var step = plan.Steps.SingleOrDefault(s => s.StepKey == stepId);
                if (step?.ControlId is "report.submit" or "report.receipt" &&
                    Services.GetService(typeof(EllaiDisplayedReceiptContext)) is EllaiDisplayedReceiptContext { Current: { } receipt })
                {
                    await progressGate.WaitAsync(ct);
                    try
                    {
                        if (!IsCurrent()) return false;
                        var progress = await ReviewClient.CompleteGuidanceReceiptAsync(session.SessionId,
                            new(proposalId, plan.PlanId, progressVersion, stepId, receipt.ReceiptId, receipt.ClientSubmissionId), ct);
                        if (!IsCurrent() || progress?.Status is not ("Ready" or "Completed") ||
                            progress.CompletedStepIds?.Contains(stepId) != true) return false;
                        progressVersion = progress.Version;
                        durableStatus = progress.Status;
                        return true;
                    }
                    finally { progressGate.Release(); }
                }
                var navigation = step is null ? null : EllaiNavigationManifest.ResolveStepDestination(step);
                var validatedInput = step?.Behavior == PlanStepBehavior.GuideHuman &&
                    EllaiReportUiManifest.All.Any(control => control.ControlId == step.ControlId &&
                        (control.ProofPolicyId == "proof-ui-validated" || control.ControlId == "report.confirmations")) &&
                    _latestGuidanceObservation?.CompletedControlIds?.Contains(step.ControlId) == true;
                var observedContext = _latestGuidanceObservation?.ContextStamp;
                // Route evidence proves navigation only, never a record read, edit or submission.
                if (!IsCurrent() || step is null ||
                    (step.Behavior != PlanStepBehavior.Explain && navigation is null && !validatedInput) ||
                    (navigation is null && !validatedInput && EllaiReportUiManifest.All.Any(control =>
                        control.ControlId == step.ControlId && control.ProofPolicyId != "proof-ui-observed")) ||
                    (navigation is null && !validatedInput && observedContext != result.ContextStamp) ||
                    (navigation is not null && _latestGuidanceObservation?.CurrentNavigationId != navigation.NavigationId) ||
                    (!validatedInput && _latestGuidanceObservation?.ControlStates?.Any(state => state.ControlId == step.ControlId &&
                        state.State == UiObservationState.Ready) != true)) return false;
                await progressGate.WaitAsync(ct);
                try
                {
                    var progress = await ReviewClient.CompleteGuidanceStepAsync(session.SessionId,
                        new(proposalId, plan.PlanId, progressVersion, stepId, observedContext!), ct);
                    if (!IsCurrent() || progress?.Status is not ("Ready" or "Completed") ||
                        progress.CompletedStepIds?.Contains(stepId) != true) return false;
                    progressVersion = progress.Version;
                    result = result with { ContextStamp = observedContext };
                    durableStatus = progress.Status;
                    return true;
                }
                finally { progressGate.Release(); }
            });
            var started = await Bonboarding.StartAsync(definition, new BonboardingStartOptions { Resume = false, StartAtStepId = result.CurrentStepId }, token);
            if (!IsCurrent()) await started.CancelAsync(CancellationToken.None);
            else
            {
                activated = true;
                ShowComposer = true;
                _generatedTour = started;
                BeginGuidanceSpeech();
                _generatedPlan = plan;
                _controlGeneratedTour = PersistControl;
                _narrateValidation = message => NarrateValidationAsync(started, progressVersion, message);
                _generatedTourChanged = (_, _) => _ = InvokeAsync(async () =>
                {
                    if (!IsCurrent() || !ReferenceEquals(started, _generatedTour)) return;
                    if (started.Status != BonboardingTourStatus.Running) EndGuidanceNarration();
                    try
                    {
                        var command = started.Status switch
                        {
                            BonboardingTourStatus.Paused => "pause",
                            BonboardingTourStatus.Cancelled or BonboardingTourStatus.Dismissed or BonboardingTourStatus.Skipped => "cancel",
                            BonboardingTourStatus.Running when durableStatus == "Paused" => "resume",
                            _ => null,
                        };
                        if (command is not null) await PersistControl(command);
                        if (!IsCurrent() || !ReferenceEquals(started, _generatedTour)) return;
                        if (durableStatus is "Completed" or "Cancelled")
                        {
                            EndGuidanceSpeech();
                            started.Changed -= _generatedTourChanged;
                            _generatedTourChanged = null;
                            _generatedTour = null;
                            _generatedPlan = null;
                            _controlGeneratedTour = null;
                            _narrateValidation = null;
                            CancelValidationNarration();
                            _guidanceProposalId = null;
                            _guidanceProposalIsAccepted = false;
                            _guidanceFeatureId = null;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (HttpRequestException) { _guidanceNotice = "Guidance could not be saved. Try again."; }
                    if (ReferenceEquals(started, _generatedTour) && started.Status == BonboardingTourStatus.Running)
                        _ = NarrateGuidanceStepAsync(started, progressVersion);
                    StateHasChanged();
                });
                started.Changed += _generatedTourChanged;
                _ = NarrateGuidanceStepAsync(started, progressVersion);
            }
        }
        catch (OperationCanceledException) { if (IsCurrent()) _guidanceNotice = "Guidance took too long. Try again."; }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or Microsoft.JSInterop.JSException)
        {
            if (IsCurrent()) _guidanceNotice = "Guidance could not open. Try again.";
        }
        finally
        {
            _preparingGuidance = false;
            var queued = _queuedGuidanceProposalId;
            _queuedGuidanceProposalId = null;
            if (!_uiObservationLifetime.IsCancellationRequested)
            {
                await InvokeAsync(StateHasChanged);
                if (queued is not null && queued != proposalId && queued == _guidanceProposalId &&
                    _sessionView?.SessionId == session.SessionId)
                    await StartProposedGuidanceAsync();
            }
        }
    }
}
