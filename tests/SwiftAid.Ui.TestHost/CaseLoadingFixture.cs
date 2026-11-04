using SwiftAid.Client.Presentation;

namespace SwiftAid.Ui.TestHost;

public sealed class CaseLoadingFixture : IProgressiveCaseWorkspaceClient
{
    private readonly TaskCompletionSource<UiCapabilityResult<CaseWorkspaceFilterOptions>> _options =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly UiCapabilityMetadata Metadata = UiCapabilityResults.SyntheticMetadata("case-workspace", "Synthetic fixture");
    private readonly TaskCompletionSource<bool> _preview = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> ListAsync(CaseWorkspaceQuery query, CancellationToken cancellationToken = default)
    {
        var row = new CaseWorkspaceRow(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "Synthetic case", "Open", "Eswatini",
            CaseWorkspaceSector.Human, null, "Assigned to you", null, DateTimeOffset.UtcNow, 1, [CaseWorkspaceAction.Open]);
        return Task.FromResult(new UiCapabilityResult<Page<CaseWorkspaceRow>>(
            UiCapabilityResultCode.Success, new Page<CaseWorkspaceRow>([row], 1, 25, 1, false), Metadata));
    }

    public Task<UiCapabilityResult<CaseWorkspaceFilterOptions>> GetFilterOptionsAsync(CancellationToken cancellationToken = default) =>
        _options.Task.WaitAsync(cancellationToken);

    public Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> ListInitialAsync(CaseWorkspaceQuery query, CancellationToken cancellationToken = default) =>
        ListAsync(query, cancellationToken);

    public async Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> EnrichAsync(Page<CaseWorkspaceRow> page, CancellationToken cancellationToken = default)
    {
        await _preview.Task.WaitAsync(cancellationToken);
        return new(UiCapabilityResultCode.Success,
            page with { Items = page.Items.Select(item => item with { ObservationPreview = "Synthetic delayed preview" }).ToArray() }, Metadata);
    }

    public void DenyOptions() => _options.TrySetResult(new(UiCapabilityResultCode.Denied, null, Metadata, "Access changed."));
    public void ReleasePreview() => _preview.TrySetResult(true);
}
