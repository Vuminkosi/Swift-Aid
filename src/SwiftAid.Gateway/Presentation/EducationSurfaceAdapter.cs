using SwiftAid.Client.Presentation;
using SwiftAid.UI.Components.Surfaces.Public;

namespace SwiftAid.Gateway.Presentation;

public sealed record EducationSurfacePage(
    IReadOnlyList<EducationMaterialPresentation> Materials,
    int TotalCount,
    bool HasMore);

/// <summary>Maps the typed education client contract into the shared UI presentation model.</summary>
public sealed class EducationSurfaceAdapter(IEducationClient client)
{
    private readonly IEducationClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<UiCapabilityResult<EducationSurfacePage>> ListAsync(
        EducationBrowseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = new EducationQuery(
            new PageRequest(request.PageNumber, request.PageSize),
            Category: request.Category,
            Language: request.Language,
            Search: request.Search,
            OfflineOnly: request.OfflineOnly);
        var result = await _client.ListAsync(query, cancellationToken).ConfigureAwait(false);
        if (result.Code != UiCapabilityResultCode.Success || result.Value is not { } page)
        {
            return new UiCapabilityResult<EducationSurfacePage>(
                result.Code,
                null,
                result.Metadata,
                result.Message);
        }

        var materials = await Task.WhenAll(page.Items.Select(material =>
            CreatePresentationAsync(material, cancellationToken))).ConfigureAwait(false);

        return new UiCapabilityResult<EducationSurfacePage>(
            result.Code,
            new EducationSurfacePage(materials, page.TotalCount, page.HasMore),
            result.Metadata,
            result.Message);
    }

    public Task<UiCapabilityResult<EducationMaterialContent>> GetContentAsync(
        EducationMaterialVersionPresentation request,
        CancellationToken cancellationToken = default) =>
        _client.GetContentAsync(ToClientRequest(request), cancellationToken);

    public Task<UiCapabilityResult<EducationDownloadResult>> DownloadAsync(
        EducationMaterialVersionPresentation request,
        CancellationToken cancellationToken = default) =>
        _client.DownloadAsync(ToClientRequest(request), cancellationToken);

    public Task<UiCapabilityResult<EducationOfflineResult>> SaveOfflineAsync(
        EducationMaterialVersionPresentation request,
        CancellationToken cancellationToken = default) =>
        _client.SaveOfflineAsync(ToClientRequest(request), cancellationToken);

    public Task<UiCapabilityResult<EducationOfflineResult>> RemoveOfflineAsync(
        EducationMaterialVersionPresentation request,
        CancellationToken cancellationToken = default) =>
        _client.RemoveOfflineAsync(ToClientRequest(request), cancellationToken);

    private async Task<EducationMaterialPresentation> CreatePresentationAsync(
        EducationMaterial material,
        CancellationToken cancellationToken)
    {
        var request = new EducationMaterialVersionRequest(material.Id, material.SourceVersion);
        var offline = await _client.GetOfflineStatusAsync(request, cancellationToken).ConfigureAwait(false);
        var offlineResult = offline.Code == UiCapabilityResultCode.Success ? offline.Value : null;
        var storageState = offlineResult switch
        {
            { IsAvailableOffline: true, StorageProvenance: EducationStorageProvenance.PersistentDeviceStore } => EducationOfflinePresentationState.SavedOnDevice,
            { StorageProvenance: EducationStorageProvenance.SessionPreview } => EducationOfflinePresentationState.SessionPreviewOnly,
            { IsAvailableOffline: false, StorageProvenance: EducationStorageProvenance.PersistentDeviceStore } => EducationOfflinePresentationState.NotSaved,
            null => EducationOfflinePresentationState.Unavailable,
            _ => EducationOfflinePresentationState.Unknown
        };

        return new EducationMaterialPresentation(
            material.Id,
            material.Title,
            material.Summary,
            material.Category,
            material.Language,
            material.Source,
            material.SourceVersion,
            material.IsApproved,
            offlineResult?.IsAvailableOffline ?? material.IsAvailableOffline,
            SourceUrl: TryGetHttpsUrl(material.Source),
            OfflineState: storageState,
            ReviewStatus: material.IsApproved ? "Approved for public display" : "Not verified for public display",
            CanSaveOffline: offlineResult?.CanSaveOffline == true,
            CanRemoveOffline: offlineResult?.CanRemoveOffline == true);
    }

    private static EducationMaterialVersionRequest ToClientRequest(EducationMaterialVersionPresentation request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new EducationMaterialVersionRequest(request.MaterialId, request.SourceVersion);
    }

    private static string? TryGetHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;
}
