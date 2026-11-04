namespace SwiftAid.Client.Presentation;

public sealed record EducationQuery(
    PageRequest Page,
    string? Category = null,
    string? Language = null,
    string? Search = null,
    bool OfflineOnly = false);

public sealed record EducationMaterial(
    string Id,
    string Title,
    string Summary,
    string Category,
    string Language,
    string Source,
    string SourceVersion,
    bool IsApproved,
    bool IsAvailableOffline,
    EducationReviewState ReviewState = EducationReviewState.Unreviewed,
    EducationReuseRights ReuseRights = EducationReuseRights.Unverified);

public sealed record EducationMaterialVersionRequest(string MaterialId, string SourceVersion);

public enum EducationReuseRights
{
    Unverified,
    PublicDomain,
    ReuseGranted,
    AttributionRequired,
    Restricted,
    AppOwnedOriginal
}

public enum EducationReviewState
{
    Unreviewed,
    ApprovedForPublic,
    DemoOnly,
    Withdrawn,
    AppAuthoredProcess
}

/// <summary>Versioned fixed text and its review, rights, and source provenance.</summary>
public sealed record EducationMaterialContent(
    string MaterialId,
    string Title,
    string TextContent,
    string Category,
    string Language,
    string Source,
    string? SourceUrl,
    string SourceVersion,
    string ContentHash,
    EducationReuseRights ReuseRights,
    EducationReviewState ReviewState,
    bool IsSyntheticNonclinical);

public enum EducationStorageProvenance
{
    None,
    SessionPreview,
    PersistentDeviceStore
}

public sealed record EducationDownloadResult(
    string MaterialId,
    string SourceVersion,
    string ContentHash,
    bool DownloadCompleted,
    EducationStorageProvenance StorageProvenance,
    string? Message = null);

public sealed record EducationOfflineResult(
    string MaterialId,
    string SourceVersion,
    string ContentHash,
    bool IsAvailableOffline,
    EducationStorageProvenance StorageProvenance,
    bool CanSaveOffline,
    bool CanRemoveOffline,
    string? Message = null);

public interface IEducationClient
{
    Task<UiCapabilityResult<Page<EducationMaterial>>> ListAsync(
        EducationQuery query,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<EducationMaterialContent>> GetContentAsync(
        EducationMaterialVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<EducationMaterialContent>("education-content"));
    }

    Task<UiCapabilityResult<EducationDownloadResult>> DownloadAsync(
        EducationMaterialVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<EducationDownloadResult>("education-download"));
    }

    Task<UiCapabilityResult<EducationOfflineResult>> GetOfflineStatusAsync(
        EducationMaterialVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<EducationOfflineResult>("education-offline"));
    }

    Task<UiCapabilityResult<EducationOfflineResult>> SaveOfflineAsync(
        EducationMaterialVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<EducationOfflineResult>("education-offline"));
    }

    Task<UiCapabilityResult<EducationOfflineResult>> RemoveOfflineAsync(
        EducationMaterialVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<EducationOfflineResult>("education-offline"));
    }
}
