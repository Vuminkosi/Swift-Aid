using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Intake.Imports;

public sealed record CaseImportLease(Guid ImportId, Guid LeaseId, ActorContext Actor, CaseImportContext Context);
public sealed record CaseImportRowOutcome(long RowNumber, Guid? CommandId, string Status, Guid? CaseId, string? ErrorCode);
public sealed record CaseImportSummary(Guid ImportId, string State, long NextRow, long Accepted, long Rejected,
    long Retryable, long Unresolved, string? ErrorCode, DateTimeOffset UpdatedAtUtc, string? OrganizationId = null);
public sealed record CaseImportBatchResult(Guid ImportId, IReadOnlyList<CaseImportRowOutcome> Rows, CaseImportSummary? Summary, string? ErrorCode);

public sealed class CaseImportException(string code) : Exception("The import operation could not be completed.")
{
    public string Code { get; } = code;
}

/// <summary>Owner-scoped durable checkpoints; registration itself remains on the existing command service.</summary>
public interface ICaseImportStore
{
    Task<CaseImportLease> OpenAsync(ActorContext actor, Guid importId, CaseImportContext context, long startRow, CancellationToken ct);
    Task<int> BeginRowAsync(CaseImportLease lease, long row, Guid? commandId, byte[] sourceHash, byte[] sourceBytes, CancellationToken ct);
    Task CompleteRowAsync(CaseImportLease lease, CaseImportRowOutcome result, CancellationToken ct);
    Task CloseAsync(CaseImportLease lease, string state, string? errorCode, CancellationToken ct);
    Task<CaseImportSummary?> GetAsync(ActorContext actor, Guid importId, CancellationToken ct);
}
