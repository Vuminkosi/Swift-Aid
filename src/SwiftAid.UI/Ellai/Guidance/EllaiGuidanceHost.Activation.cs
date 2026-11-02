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
    private string? _guidanceNotice;
    private bool _preparingGuidance;
    private IBonboardingTourSession? _generatedTour;
    private ValidatedGuidancePlan? _generatedPlan;
    private bool _advancingValidatedInput;
    private EventHandler<BonboardingTourSessionChangedEventArgs>? _generatedTourChanged;
    private Func<string, Task<bool>>? _controlGeneratedTour;

    private async Task AdvanceValidatedInputAsync(CancellationToken ct)
    {
        var tour = _generatedTour;
        if (_advancingValidatedInput || tour is null || _generatedPlan is null ||
            tour.Status is not (BonboardingTourStatus.Running or BonboardingTourStatus.Waiting)) return;
        var step = _generatedPlan.Steps.SingleOrDefault(item => item.StepKey == tour.CurrentStep?.Id);
        if (step?.Behavior != PlanStepBehavior.GuideHuman ||
            !EllaiReportUiManifest.All.Any(control => control.ControlId == step.ControlId && control.ProofPolicyId == "proof-ui-validated") ||
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

    private async Task ControlGeneratedTourAsync(string command)
    {
        var tour = _generatedTour;
        if (tour is null || _controlGeneratedTour is null) return;
        if (!await _controlGeneratedTour(command)) return;
        if (!ReferenceEquals(tour, _generatedTour)) return;
        if (command == "pause") await tour.PauseAsync(CancellationToken.None);
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
                if (command == "stop-speech") StopResponseAudio();
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
                if (command is "resume" or "next")
                {
                    if (command == "next" && saved.Status != "Ready")
                    {
                        _guidanceNotice = "Resume the guidance before moving to the next step.";
                        return;
                    }
                    await StartProposedGuidanceAsync();
                    if (command == "next" && controlId == _lastGuidanceControlProposalId &&
                        _sessionView?.SessionId == session.SessionId)
                        await AdvanceGuidanceStepAsync(_uiObservationLifetime.Token);
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
        var tour = _generatedTour;
        _generatedTour = null;
        _generatedPlan = null;
        _controlGeneratedTour = null;
        if (tour is null) return;
        if (_generatedTourChanged is not null) tour.Changed -= _generatedTourChanged;
        _generatedTourChanged = null;
        try { await tour.CancelAsync(CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Microsoft.JSInterop.JSDisconnectedException) { }
    }

    private async Task StartProposedGuidanceAsync()
    {
        if (_preparingGuidance || _sessionView is not { Status: "Active" } session ||
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
                    ? EllaiNavigationManifest.ResolveRoute(EllaiSurveillanceUiManifest.NavigationId) : null;
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
            var anchors = EllaiReportUiManifest.All.Concat(EllaiNavigationManifest.Controls).Concat(EllaiDashboardUiManifest.Controls).Concat(EllaiCaseListUiManifest.Controls).Concat(EllaiCaseDetailUiManifest.Controls).Concat(EllaiCommunityUiManifest.Controls).Concat(EllaiSurveillanceUiManifest.Controls)
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
                    if (command == "resume") await ReportCurrentUiAsync(_uiObservationLifetime.Token);
                    var target = command switch { "pause" => "Paused", "resume" => "Ready", _ => "Cancelled" };
                    if (durableStatus == target) return true;
                    var update = await ReviewClient.ControlGuidanceAsync(session.SessionId,
                        new(proposalId, plan.PlanId, progressVersion, command), _uiObservationLifetime.Token);
                    if (!IsCurrent() || update?.Status != target)
                    {
                        _guidanceNotice = "Guidance could not be saved. Try again.";
                        return false;
                    }
                    progressVersion = update.Version;
                    durableStatus = update.Status;
                    _guidanceNotice = null;
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
                var navigation = step is null ? null : EllaiNavigationManifest.ResolveStepDestination(step);
                var validatedInput = step?.Behavior == PlanStepBehavior.GuideHuman &&
                    EllaiReportUiManifest.All.Any(control => control.ControlId == step.ControlId && control.ProofPolicyId == "proof-ui-validated") &&
                    _latestGuidanceObservation?.CompletedControlIds?.Contains(step.ControlId) == true;
                var observedContext = _latestGuidanceObservation?.ContextStamp;
                // Route evidence proves navigation only, never a record read, edit or submission.
                if (!IsCurrent() || step is null ||
                    (step.Behavior != PlanStepBehavior.Explain && navigation is null && !validatedInput) ||
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
                _generatedTour = started;
                _generatedPlan = plan;
                _controlGeneratedTour = PersistControl;
                _generatedTourChanged = (_, _) => _ = InvokeAsync(async () =>
                {
                    if (!IsCurrent() || !ReferenceEquals(started, _generatedTour)) return;
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
                            started.Changed -= _generatedTourChanged;
                            _generatedTourChanged = null;
                            _generatedTour = null;
                            _generatedPlan = null;
                            _controlGeneratedTour = null;
                            _guidanceProposalId = null;
                            _guidanceProposalIsAccepted = false;
                            _guidanceFeatureId = null;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (HttpRequestException) { _guidanceNotice = "Guidance could not be saved. Try again."; }
                    StateHasChanged();
                });
                started.Changed += _generatedTourChanged;
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
            if (!_uiObservationLifetime.IsCancellationRequested) await InvokeAsync(StateHasChanged);
        }
    }
}
