using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases.Commands;

/// <summary>Server-owned access requirements derived from a closed case-command type.</summary>
public static class CaseCommandAuthorizationMap
{
    public static CaseCommandAuthorizationRequirement Resolve(CaseCommand command, CaseAggregate replayedAggregate)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(replayedAggregate);

        return command switch
        {
            AcknowledgeAssignmentCommand => Requirement(CaseOperation.AcknowledgeAssignment, CaseCapability.AcknowledgeAssignment),
            GrantCaseCollaboratorCommand or RevokeCaseCollaboratorCommand => Requirement(CaseOperation.ManageCollaborators, CaseCapability.ManageCollaborators),
            CorrectReportCommand => Requirement(CaseOperation.CorrectReport, CaseCapability.CorrectReport),
            RecordObservationCommand => Requirement(CaseOperation.RecordObservation, CaseCapability.RecordObservation),
            ScheduleFollowUpCommand or RecordFollowUpOutcomeCommand => Requirement(CaseOperation.ManageFollowUp, CaseCapability.ManageFollowUp),
            RaiseEscalationCommand or AcknowledgeEscalationCommand or CloseEscalationCommand => Requirement(CaseOperation.ManageEscalation, CaseCapability.ManageEscalation),
            RequestCaseActionCommand request => ResolveRequest(request),
            ReviewCaseActionRequestCommand review => ResolveReview(review, replayedAggregate),
            ResolveCaseCommand => Requirement(CaseOperation.ResolveCase, CaseCapability.ResolveCase),
            ReopenCaseCommand => Requirement(CaseOperation.ReopenCase, CaseCapability.ReopenCase),
            AcceptCaseCommand => throw new NotSupportedException("Case creation is not supported by the non-creation command authorization map."),
            _ => throw new NotSupportedException($"Command type {command.GetType().Name} is not supported.")
        };
    }

    private static CaseCommandAuthorizationRequirement ResolveRequest(RequestCaseActionCommand request)
    {
        if (!Enum.IsDefined(request.Action))
        {
            throw new InvalidDataException("The protected-action request uses an unknown operation.");
        }

        return Requirement(CaseOperation.RequestProtectedAction, CaseCapability.RequestProtectedAction);
    }

    private static CaseCommandAuthorizationRequirement ResolveReview(
        ReviewCaseActionRequestCommand review,
        CaseAggregate replayedAggregate)
    {
        if (!Enum.IsDefined(review.Outcome))
        {
            throw new InvalidDataException("The protected-action review uses an unknown outcome.");
        }

        var request = replayedAggregate.Events
            .OfType<CaseActionRequested>()
            .SingleOrDefault(item => item.RequestId == review.RequestId);
        if (request is null || !Enum.IsDefined(request.RequestedOperation))
        {
            throw new InvalidDataException("The reviewed request is missing or has an unknown operation in the authoritative case history.");
        }

        if (review.Outcome != CaseActionReviewOutcome.Approved)
        {
            return Requirement(CaseOperation.ReviewProtectedAction, CaseCapability.ReviewProtectedAction);
        }

        var actionRequirement = request.RequestedOperation switch
        {
            CaseAction.Reassign or CaseAction.Assist => (CaseOperation.AssignCase, CaseCapability.AssignCase),
            CaseAction.Resolve => (CaseOperation.ResolveCase, CaseCapability.ResolveCase),
            CaseAction.Reopen => (CaseOperation.ReopenCase, CaseCapability.ReopenCase),
            _ => throw new InvalidDataException("The approved request operation is outside the supported set.")
        };

        return Requirement(
            [CaseOperation.ReviewProtectedAction, actionRequirement.Item1],
            CaseCapability.ReviewProtectedAction | actionRequirement.Item2);
    }

    private static CaseCommandAuthorizationRequirement Requirement(
        CaseOperation operation,
        CaseCapability capability) => Requirement([operation], capability);

    private static CaseCommandAuthorizationRequirement Requirement(
        IEnumerable<CaseOperation> operations,
        CaseCapability capabilities)
    {
        var snapshot = operations.Distinct().OrderBy(operation => (int)operation).ToArray();
        return new CaseCommandAuthorizationRequirement(Array.AsReadOnly(snapshot), capabilities);
    }
}

public sealed record CaseCommandAuthorizationRequirement(
    IReadOnlyList<CaseOperation> RequiredOperations,
    CaseCapability RequiredCapabilities);
