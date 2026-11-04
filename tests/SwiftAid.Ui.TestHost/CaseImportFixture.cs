using SwiftAid.Client.Intake.Imports;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Intake;
using SwiftAid.Contracts.Intake.Imports;

namespace SwiftAid.Ui.TestHost;

public sealed class CaseImportFixture : IWorkspaceImportClient, IPublicIntakeMetadataClient
{
    private WorkspaceImportSummary? _summary;
    private WorkspaceImportContext? _context;
    private bool _denied;
    public void Revoke() => _denied = true;
    public void SeedPaused(bool fingerprinted = false)
    {
        _summary = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            "Paused", 2, 1, 0, 0, 0, null, DateTimeOffset.UtcNow, "org-1");
        var row = string.Join(',', CaseImportCsvSchema.Columns.Select(_ => "synthetic"));
        var csv = string.Join(',', CaseImportCsvSchema.Columns) + "\n" + row + "\n" + row + "\n";
        var hash = fingerprinted ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(csv))).ToLowerInvariant() : null;
        _context = new("workspace-1", new(true, "SZ", "1", true, true, "/privacy", "1", null, null, null), "org-1", hash);
    }
    public Task<IReadOnlyList<WorkspaceImportScope>> GetWorkspacesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkspaceImportScope>>([new("workspace-1", "org-1", "SZ")]);
    public Task<IReadOnlyList<WorkspaceImportHistoryItem>> GetRecentAsync(WorkspaceImportScope scope, CancellationToken cancellationToken = default) =>
        _denied ? Task.FromException<IReadOnlyList<WorkspaceImportHistoryItem>>(new WorkspaceImportException("not-authorized")) :
            Task.FromResult<IReadOnlyList<WorkspaceImportHistoryItem>>(_summary is null ? [] : [new(_summary.ImportId, scope.WorkspaceId, scope.CountryCode, _summary.State, _summary.UpdatedAtUtc)]);
    public Task<WorkspaceImportSummary?> GetAsync(Guid importId, CancellationToken cancellationToken = default) => _denied
        ? Task.FromException<WorkspaceImportSummary?>(new WorkspaceImportException("not-authorized"))
        : Task.FromResult(_summary?.ImportId == importId ? _summary : null);
    public Task<bool> MatchesContextAsync(Guid importId, WorkspaceImportContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(_summary?.ImportId == importId && _context == context);
    public Task<IReadOnlyList<WorkspaceImportRow>> GetIssuesAsync(Guid importId, long afterRow, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkspaceImportRow>>([]);
    public Task<WorkspaceImportBatchResult> ImportAsync(Guid importId, WorkspaceImportContext context, CaseCsvBatch batch, CancellationToken cancellationToken = default)
    {
        if (_context is not null && _context != context) throw new WorkspaceImportException("import-context-conflict");
        _context = context;
        _summary = new(importId, batch.IsFinal ? "Completed" : "Paused", batch.StartRow + batch.RowCount,
            (_summary?.Accepted ?? 0) + batch.RowCount, 0, 0, 0, null, DateTimeOffset.UtcNow, context.OrganizationId);
        return Task.FromResult(new WorkspaceImportBatchResult(importId, [], _summary, null));
    }
    public Task<UiCapabilityResult<PublicIntakeMetadata>> GetMetadataAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(UiCapabilityResults.Unavailable<PublicIntakeMetadata>("fixture"));
    public Task<UiCapabilityResult<PublicIntakeCountryCatalogue>> GetCountriesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new UiCapabilityResult<PublicIntakeCountryCatalogue>(UiCapabilityResultCode.Success,
            new("1", "/countries", [new("SZ", "Eswatini")]),
            new("fixture", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true)));
}
