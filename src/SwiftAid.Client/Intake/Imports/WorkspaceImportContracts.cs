using SwiftAid.Contracts.Intake;
using SwiftAid.Contracts.Intake.Imports;

namespace SwiftAid.Client.Intake.Imports;

public sealed record WorkspaceImportScope(string WorkspaceId, string OrganizationId, string CountryCode);
public sealed record WorkspaceImportHistoryItem(Guid ImportId, string WorkspaceId, string CountryCode, string State, DateTimeOffset UpdatedAtUtc);
public sealed record WorkspaceImportContext(string WorkspaceId, IntakeConfirmations Confirmations,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? OrganizationId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? FileSha256 = null);
public sealed record WorkspaceImportRow(long RowNumber, Guid? CommandId, string Status, Guid? CaseId, string? ErrorCode);
public sealed record WorkspaceImportSummary(Guid ImportId, string State, long NextRow, long Accepted, long Rejected,
    long Retryable, long Unresolved, string? ErrorCode, DateTimeOffset UpdatedAtUtc, string? OrganizationId = null);
public sealed record WorkspaceImportBatchResult(Guid ImportId, IReadOnlyList<WorkspaceImportRow> Rows, WorkspaceImportSummary? Summary, string? ErrorCode);

public interface IWorkspaceImportClient
{
    Task<IReadOnlyList<WorkspaceImportScope>> GetWorkspacesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkspaceImportHistoryItem>> GetRecentAsync(WorkspaceImportScope scope, CancellationToken cancellationToken = default);
    Task<WorkspaceImportSummary?> GetAsync(Guid importId, CancellationToken cancellationToken = default);
    Task<bool> MatchesContextAsync(Guid importId, WorkspaceImportContext context, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkspaceImportRow>> GetIssuesAsync(Guid importId, long afterRow, CancellationToken cancellationToken = default);
    Task<WorkspaceImportBatchResult> ImportAsync(Guid importId, WorkspaceImportContext context, CaseCsvBatch batch,
        CancellationToken cancellationToken = default);
}

public sealed class WorkspaceImportException(string code) : Exception("The import could not be completed.")
{
    public string Code { get; } = code;
}
