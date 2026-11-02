using Bit.Bonboarding;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

public partial class EllaiGuidanceHost
{
    private string? _guidanceProposalId;
    private string? _guidanceProposalTurn;
    private string? _pendingGuidanceTurn;
    private string? _guidanceNotice;
    private bool _preparingGuidance;
    private IBonboardingTourSession? _generatedTour;

    private void ObserveGuidanceProposal(EllaiConversationTurnDto turn, bool newlyCompleted)
    {
        if (_guidanceProposalTurn != turn.ClientUtteranceId)
        {
            _guidanceProposalId = null;
            _guidanceProposalTurn = turn.ClientUtteranceId;
            _guidanceNotice = null;
            _ = CancelGeneratedGuidanceAsync();
        }
        if (turn.Outcome is "Queued" or "Processing") _pendingGuidanceTurn = turn.ClientUtteranceId;
        if (turn.Outcome != "Completed") return;
        newlyCompleted |= _pendingGuidanceTurn == turn.ClientUtteranceId;
        _pendingGuidanceTurn = null;
        var proposal = turn.UiProposals.FirstOrDefault(p => p.Proposal.Kind == UiIntentKind.StartGuidance);
        if (proposal is null || _guidanceProposalId == proposal.ProposalId) return;
        _guidanceProposalId = proposal.ProposalId;
        // Conversation restoration offers an explicit action; only the currently awaited turn auto-starts.
        if (newlyCompleted) _ = StartProposedGuidanceAsync();
    }

    private async Task CancelGeneratedGuidanceAsync()
    {
        var tour = _generatedTour;
        _generatedTour = null;
        if (tour is null) return;
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
        var turnId = _guidanceProposalTurn;
        bool IsCurrent() => !_uiObservationLifetime.IsCancellationRequested &&
            _sessionView?.SessionId == session.SessionId && _sessionView.Status == "Active" &&
            _guidanceProposalId == proposalId && _guidanceProposalTurn == turnId;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_uiObservationLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(100));
        var token = timeout.Token;
        try
        {
            await ReportCurrentUiAsync(token);
            if (!IsCurrent()) return;
            var result = await ReviewClient.PrepareGuidancePlanAsync(session.SessionId, proposalId, token);
            if (!IsCurrent()) return;
            if (result?.Status == "NeedsNavigation" && result.NavigationId == EllaiReportUiManifest.NavigationId)
            {
                // This is a registered public destination, never a model-supplied URL.
                Navigation.NavigateTo("/report");
                for (var attempt = 0; attempt < 40 && IsCurrent(); attempt++)
                {
                    await Task.Delay(250, token);
                    await ReportCurrentUiAsync(token);
                    if (_latestGuidanceObservation?.MountedControlIds.Contains("report.entry") == true) break;
                }
                if (!IsCurrent()) return;
                result = await ReviewClient.PrepareGuidancePlanAsync(session.SessionId, proposalId, token);
            }
            if (!IsCurrent()) return;
            await ReportCurrentUiAsync(token);
            if (!IsCurrent() || result?.Plan is not { } plan || result.Status is not ("Ready" or "Paused") ||
                _latestGuidanceObservation?.ContextStamp != result.ContextStamp)
            {
                _guidanceNotice = "Guidance is not ready yet. Try again when the page has loaded.";
                return;
            }
            var anchors = EllaiReportUiManifest.All.ToDictionary(c => c.ControlId, c => c.AnchorBindingKey);
            if (plan.Steps.Any(step => step.ControlId is null || !anchors.ContainsKey(step.ControlId)))
            {
                _guidanceNotice = "This guidance is not available on the current page.";
                return;
            }
            var progressVersion = result.ProgressVersion;
            var definition = new EllaiBonboardingCompiler().Compile(plan, anchors, async (stepId, ct) =>
            {
                if (!IsCurrent()) return false;
                await ReportCurrentUiAsync(ct);
                var admission = await ReviewClient.ReadGuidanceAdmissionAsync(session.SessionId, proposalId, ct);
                if (admission?.Status != "Ready") return false;
                var step = plan.Steps.SingleOrDefault(s => s.StepKey == stepId);
                // Observation can prove an explanation target exists, never a form edit or submission.
                if (!IsCurrent() || step?.Behavior != PlanStepBehavior.Explain ||
                    _latestGuidanceObservation?.ContextStamp != result.ContextStamp ||
                    _latestGuidanceObservation?.ControlStates?.Any(state => state.ControlId == step.ControlId &&
                        state.State == UiObservationState.Ready) != true) return false;
                var progress = await ReviewClient.CompleteGuidanceStepAsync(session.SessionId,
                    new(proposalId, plan.PlanId, progressVersion, stepId, result.ContextStamp!), ct);
                if (!IsCurrent() || progress?.Status is not ("Ready" or "Completed") ||
                    progress.CompletedStepIds?.Contains(stepId) != true) return false;
                progressVersion = progress.Version;
                return true;
            });
            var started = await Bonboarding.StartAsync(definition, new BonboardingStartOptions { Resume = false, StartAtStepId = result.CurrentStepId }, token);
            if (!IsCurrent()) await started.CancelAsync(CancellationToken.None);
            else _generatedTour = started;
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
