using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Cases;

/// <summary>Interactive web adapter. No model tool or autonomous agent receives these operations.</summary>
public sealed class OfficerEscalationReviewGatewayClient(AuthenticationStateProvider authentication, IServiceProvider services)
    : IOfficerEscalationReviewClient
{
    public async Task<OfficerEscalationReviewContext?> GetAsync(Guid caseId, CancellationToken ct = default)
    {
        try
        {
            var actor = await ActorAsync().ConfigureAwait(false);
            await using var scope = services.CreateAsyncScope();
            var query = scope.ServiceProvider.GetService<IHealthOfficerEscalationReviewQuery>();
            var result = query is null ? null : await query.GetAsync(actor, caseId, ct).ConfigureAwait(false);
            return result is null ? null : new(result.CaseVersion,
                result.Escalations.Select(item => new OfficerEscalationReviewItem(item.EscalationId, item.RaisedAtUtc)).ToArray(), result.MoreEscalations,
                result.Officers.Select(item => new EligibleHealthOfficerCandidate(item.DesignationId, item.DesignationVersion, item.ActorId, item.DisplayLabel)).ToArray(), result.MoreOfficers);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    public async Task<OfficerEscalationActionResult> ApproveAsync(OfficerEscalationReviewedSelection selection, CancellationToken ct = default)
    {
        var attempted = false;
        try
        {
            var actor = await ActorAsync().ConfigureAwait(false);
            await using var scope = services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetService<IHealthOfficerEscalationApprovalService>();
            if (service is null) return new(OfficerEscalationActionStatus.Unavailable);
            attempted = true;
            var result = await service.ApproveAsync(actor, new(selection.ApprovalId, selection.ApprovalCommandId, selection.CaseId,
                selection.EscalationId, selection.CaseVersion, selection.OfficerActorId, selection.DesignationId, selection.DesignationVersion,
                selection.FollowUpId, selection.AcknowledgementDueUtc, selection.FollowUpDueUtc), ct).ConfigureAwait(false);
            return result.Status switch
            {
                HealthOfficerEscalationApprovalStatus.Recorded or HealthOfficerEscalationApprovalStatus.Replayed when result.ApprovalId == selection.ApprovalId =>
                    new(result.Status == HealthOfficerEscalationApprovalStatus.Recorded ? OfficerEscalationActionStatus.Recorded : OfficerEscalationActionStatus.Replayed),
                HealthOfficerEscalationApprovalStatus.Recorded or HealthOfficerEscalationApprovalStatus.Replayed => new(OfficerEscalationActionStatus.OutcomeUnknown),
                HealthOfficerEscalationApprovalStatus.Denied => new(OfficerEscalationActionStatus.Denied),
                HealthOfficerEscalationApprovalStatus.Conflict => new(OfficerEscalationActionStatus.Conflict),
                HealthOfficerEscalationApprovalStatus.Invalid => new(OfficerEscalationActionStatus.Invalid),
                _ => new(OfficerEscalationActionStatus.Unavailable)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !attempted) { throw; }
        catch (UnauthorizedAccessException) when (!attempted) { return new(OfficerEscalationActionStatus.Denied); }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(attempted ? OfficerEscalationActionStatus.OutcomeUnknown : OfficerEscalationActionStatus.Unavailable); }
    }

    public async Task<OfficerEscalationActionResult> AssignAsync(OfficerEscalationReviewedSelection selection, CancellationToken ct = default)
    {
        var attempted = false;
        try
        {
            var actor = await ActorAsync().ConfigureAwait(false);
            await using var scope = services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetService<IHealthOfficerEscalationAssignmentService>();
            if (service is null) return new(OfficerEscalationActionStatus.Unavailable);
            attempted = true;
            var result = await service.AssignAsync(actor, new(selection.CaseId, selection.ApprovalId, selection.AssignmentCommandId), ct).ConfigureAwait(false);
            return result.Status switch
            {
                HealthOfficerEscalationAssignmentStatus.Recorded or HealthOfficerEscalationAssignmentStatus.Replayed
                    when result.FollowUpEventId is { } id && id != Guid.Empty && result.CaseVersion is > 0 =>
                    new(result.Status == HealthOfficerEscalationAssignmentStatus.Recorded ? OfficerEscalationActionStatus.Recorded : OfficerEscalationActionStatus.Replayed, id, result.CaseVersion),
                HealthOfficerEscalationAssignmentStatus.Recorded or HealthOfficerEscalationAssignmentStatus.Replayed => new(OfficerEscalationActionStatus.OutcomeUnknown),
                HealthOfficerEscalationAssignmentStatus.Denied => new(OfficerEscalationActionStatus.Denied),
                HealthOfficerEscalationAssignmentStatus.Conflict => new(OfficerEscalationActionStatus.Conflict),
                HealthOfficerEscalationAssignmentStatus.Invalid => new(OfficerEscalationActionStatus.Invalid),
                _ => new(OfficerEscalationActionStatus.Unavailable)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !attempted) { throw; }
        catch (UnauthorizedAccessException) when (!attempted) { return new(OfficerEscalationActionStatus.Denied); }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(attempted ? OfficerEscalationActionStatus.OutcomeUnknown : OfficerEscalationActionStatus.Unavailable); }
    }

    private async Task<ActorContext> ActorAsync() => ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User);
}
