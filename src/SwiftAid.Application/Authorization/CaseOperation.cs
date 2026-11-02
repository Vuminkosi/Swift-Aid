namespace SwiftAid.Application.Authorization;

/// <summary>Protected operations evaluated by the shared case resource policy.</summary>
public enum CaseOperation
{
    ReadCase,
    ReadAttachment,
    Synchronize,
    Search,
    Export,
    Count,
    RequestProtectedAction,
    ExecuteProtectedAction,
    ReadLinkedSignalEvidence,
    CorrectReport = 9,
    ReviewProtectedAction = 10,
    AssignCase = 11,
    ResolveCase = 12,
    ReopenCase = 13,
    /// <summary>
    /// Records a reviewer decision on a guest intake report. Evaluated only by the intake
    /// review authorizer against the receiving organization's grant groups, never by the
    /// shared case resource policy.
    /// </summary>
    ReviewIntake = 14,
    /// <summary>
    /// Coordinates an unrouted guest intake report (PC-09). Evaluated only by the intake
    /// coordination authorizer against a grant in the configured platform coordination
    /// organization, never by the shared case resource policy. Grants no case, clinical,
    /// or coverage authority.
    /// </summary>
    CoordinateIntake = 15,
    /// <summary>Appends a staff-recorded factual observation to an active managed case.</summary>
    RecordObservation = 16,
    /// <summary>Schedules follow-up work and records its outcome on an active managed case.</summary>
    ManageFollowUp = 17,
    /// <summary>Raises, acknowledges, and closes staff-owned escalations on an active managed case.</summary>
    ManageEscalation = 18,
    /// <summary>Records the accountable owner's acknowledgement of the current assignment.</summary>
    AcknowledgeAssignment = 19,
    /// <summary>Creates and revokes explicit, bounded collaborator grants.</summary>
    ManageCollaborators = 20,
    /// <summary>Creates, updates, and closes case assistance requests.</summary>
    ManageAssistance = 21
}
