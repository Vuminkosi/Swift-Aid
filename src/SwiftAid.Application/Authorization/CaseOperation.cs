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
    ReviewIntake = 14
}
