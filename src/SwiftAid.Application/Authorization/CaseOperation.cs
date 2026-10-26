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
    ReadLinkedSignalEvidence
}
