namespace SwiftAid.UI.Components.Surfaces.Public;

/// <summary>View-only material metadata used by the shared education surface.</summary>
public sealed record EducationMaterialPresentation(
    string Id,
    string Title,
    string Summary,
    string Category,
    string Language,
    string Source,
    string SourceVersion,
    bool IsApproved,
    bool IsAvailableOffline,
    string? SourceUrl = null,
    EducationOfflinePresentationState OfflineState = EducationOfflinePresentationState.Unavailable,
    string? ReviewStatus = null,
    string? ReuseRights = null,
    string? Content = null,
    string? ContentHash = null,
    bool IsContentLoading = false,
    string? ContentStatus = null,
    bool ContentStatusIsError = false,
    bool CanDownload = false,
    bool IsDownloadPending = false,
    bool CanSaveOffline = false,
    bool CanRemoveOffline = false,
    bool IsOfflineActionPending = false);

public enum EducationOfflinePresentationState
{
    Unavailable,
    Unknown,
    NotSaved,
    SessionPreviewOnly,
    SavedOnDevice
}

public sealed record EducationMaterialVersionPresentation(string MaterialId, string SourceVersion);

/// <summary>Typed list filters mapped by each host to its education client.</summary>
public sealed record EducationBrowseRequest(
    int PageNumber = 1,
    int PageSize = 12,
    string? Category = null,
    string? Language = null,
    string? Search = null,
    bool OfflineOnly = false);
