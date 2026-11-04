namespace SwiftAid.UI.Components.Sessions;

/// <summary>
/// UI-safe projection of the current session management permissions. The host maps
/// this from its capability client; the shared UI does not infer permissions.
/// </summary>
public sealed record SessionManagementPresentation(
    bool IsAvailable,
    bool IsCurrent,
    Guid? SessionId,
    long? Version,
    string? Status,
    bool CanCreate,
    bool CanEdit,
    bool CanReview,
    bool CanSchedule,
    bool CanCancel,
    bool CanComplete,
    string? Message = null,
    bool CanViewHistory = false,
    IReadOnlyList<string>? OwnerOrganizationIds = null)
{
    public static SessionManagementPresentation Unavailable(string? message = null) => new(
        IsAvailable: false,
        IsCurrent: false,
        SessionId: null,
        Version: null,
        Status: null,
        CanCreate: false,
        CanEdit: false,
        CanReview: false,
        CanSchedule: false,
        CanCancel: false,
        CanComplete: false,
        Message: message);

    public bool Matches(Guid? sessionId, long? version, string? status) =>
        IsAvailable &&
        IsCurrent &&
        SessionId == sessionId &&
        Version == version &&
        string.Equals(Status, status, StringComparison.OrdinalIgnoreCase);
}
