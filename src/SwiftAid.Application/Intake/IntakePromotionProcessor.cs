using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Intake;

/// <summary>Executes reviewer promotion of an intake receipt into a managed case.</summary>
/// <remarks>
/// Promotion is an idempotent saga over two idempotent participants, not one distributed
/// transaction: registration is keyed by a receipt-derived command identifier, so every
/// retry of the same promotion replays the same case, and the promotion link is recorded
/// exactly once per receipt. The receipt lock in the apply step serializes concurrent
/// officers: the loser converges on the winner's case ("two accepting officers create one
/// promotion"). Registration authorization failures surface as
/// <c>NotFoundOrNotAuthorized</c> — the reviewer holds no promotion scope — while
/// infrastructure failures throw <see cref="IntakePromotionUnavailableException"/> for a
/// safe retry. An ambiguous workspace scope (several eligible workspaces, none chosen)
/// conflicts instead of silently landing the case in the wrong workspace.
/// </remarks>
public sealed class IntakePromotionProcessor(
    IIntakePromotionSource source,
    IIntakeSubmissionOpener opener,
    IIntakePromotionWorkspaceResolver workspaces,
    IManagedCaseRegistrationProcessor registration,
    IIntakeDecisionStore promotions,
    TimeProvider timeProvider,
    ICaseSummaryQuery? caseSummary = null) : IIntakePromotionService
{
    private readonly IIntakePromotionSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly IIntakeSubmissionOpener _opener = opener ?? throw new ArgumentNullException(nameof(opener));
    private readonly IIntakePromotionWorkspaceResolver _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
    private readonly IManagedCaseRegistrationProcessor _registration = registration ?? throw new ArgumentNullException(nameof(registration));
    private readonly IIntakeDecisionStore _promotions = promotions ?? throw new ArgumentNullException(nameof(promotions));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ICaseSummaryQuery? _caseSummary = caseSummary;

    public async Task<IntakeDecisionResult> PromoteAsync(
        IntakeDecision command,
        ActorContext actor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);
        if (command.ReceiptId == Guid.Empty || command.ExpectedVersion < 1 ||
            command.Decision != IntakeDecisionKind.AcceptIntoCase ||
            string.IsNullOrWhiteSpace(actor.ActorId))
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.Invalid, null);
        }

        if (command.TargetCaseId is not null)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.Invalid, null);
        }

        var data = await _source.ReadSourceAsync(command.ReceiptId, ct).ConfigureAwait(false);
        if (data is null)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.NotFoundOrNotAuthorized, null);
        }

        var (status, next) = IntakeDecisionTransition.Evaluate(
            command, data.Status, data.Version, data.RoutingStatus, data.IntakeQueueId);
        if (status != IntakeDecisionStatus.Applied || next is null)
        {
            return new IntakeDecisionResult(status, null);
        }

        if (next == data.Status)
        {
            return await ReplayAsync(command, data, actor, ct).ConfigureAwait(false);
        }

        var scope = OpenPromotionScope(
            command.ReceiptId,
            await _opener.OpenAsync(data.ProtectedPayload, ct).ConfigureAwait(false));
        if (scope is null)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.Invalid, null);
        }

        var eligible = await _workspaces.FindEligibleWorkspacesAsync(
            actor, data.ReceivingOrganizationId, scope.CountryCode, scope.Sector, ct).ConfigureAwait(false);
        if (eligible.Count == 0)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.NotFoundOrNotAuthorized, null);
        }

        if (eligible.Count != 1)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.Conflict, null);
        }

        var registered = await RegisterAsync(
            actor,
            new RegisterManagedCaseRequest(IntakePromotionIds.CommandId(command.ReceiptId), eligible[0], scope.Report),
            ct).ConfigureAwait(false);
        if (registered.Status == CaseRegistrationStatus.Denied)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.NotFoundOrNotAuthorized, null);
        }

        if (registered.Status == CaseRegistrationStatus.Conflict)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.Conflict, null);
        }

        if (registered.Status != CaseRegistrationStatus.Accepted &&
            registered.Status != CaseRegistrationStatus.Replayed)
        {
            throw new IntakePromotionUnavailableException();
        }

        if (registered.CaseId is not { } caseId || caseId == Guid.Empty)
        {
            throw new IntakePromotionUnavailableException();
        }

        var applied = await ApplyAsync(
            new IntakePromotionApplication(
                command.ReceiptId,
                command.ExpectedVersion,
                caseId,
                actor.ActorId,
                data.Version,
                actor.ActorId,
                command.ReviewReason),
            ct).ConfigureAwait(false);
        if (applied.Status == IntakeDecisionStatus.Applied)
        {
            return applied;
        }

        if (applied.Status == IntakeDecisionStatus.Conflict)
        {
            return await ReplayAsync(command, data, actor, ct).ConfigureAwait(false);
        }

        return applied;
    }

    private async Task<IntakeDecisionResult> ReplayAsync(
        IntakeDecision command,
        IntakePromotionSource data,
        ActorContext actor,
        CancellationToken ct)
    {
        var promoted = await _source.FindPromotedCaseIdAsync(command.ReceiptId, ct).ConfigureAwait(false);
        if (promoted is not { } caseId || caseId == Guid.Empty)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.Conflict, null);
        }

        // A review grant does not grant access to the managed case created by
        // another officer. The summary query rechecks ReadCase under a case lease.
        if (_caseSummary is null ||
            (await _caseSummary.GetAsync(actor, caseId, ct).ConfigureAwait(false)) is not
                { Access: CaseSummaryAccess.Allowed, Data.CaseId: var authorizedCaseId } || authorizedCaseId != caseId)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.NotFoundOrNotAuthorized, null);
        }

        return new IntakeDecisionResult(
            IntakeDecisionStatus.Applied,
            new IntakeReceipt(
                data.ReceiptId,
                data.ClientSubmissionId,
                IntakeStatus.AcceptedIntoCase,
                data.RoutingStatus,
                data.ReceivedAtUtc),
            caseId);
    }

    private sealed record PromotionScope(CaseReportEventData Report, string CountryCode, Sector Sector);

    private static PromotionScope? OpenPromotionScope(Guid receiptId, IntakeSubmission submission)
    {
        try
        {
            var report = IntakePromotionMapper.ToReport(receiptId, submission);
            return report switch
            {
                CaseReportEventData.HumanV2 human => new PromotionScope(
                    report, human.Confirmations.OccurrenceCountryCode, Sector.HumanHealth),
                CaseReportEventData.AnimalV2 animal => new PromotionScope(
                    report, animal.Confirmations.OccurrenceCountryCode, Sector.AnimalHealth),
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<CaseRegistrationResult> RegisterAsync(
        ActorContext actor,
        RegisterManagedCaseRequest request,
        CancellationToken ct)
    {
        try
        {
            return await _registration.RegisterAsync(actor, request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new IntakePromotionUnavailableException();
        }
    }

    private async Task<IntakeDecisionResult> ApplyAsync(
        IntakePromotionApplication application,
        CancellationToken ct)
    {
        try
        {
            return await _promotions.ApplyPromotionAsync(
                application, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (IntakePromotionUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new IntakePromotionUnavailableException();
        }
    }
}
