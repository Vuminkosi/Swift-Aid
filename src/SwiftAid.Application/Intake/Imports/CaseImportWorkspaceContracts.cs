using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Intake.Imports;

public sealed record CaseImportWorkspace(string WorkspaceId, string OrganizationId, string CountryCode);
public sealed record CaseImportHistoryItem(Guid ImportId, string WorkspaceId, string CountryCode, string State, DateTimeOffset UpdatedAtUtc);

/// <summary>Current authorized destinations and owner-only import history, bounded for the workspace UI.</summary>
public interface ICaseImportWorkspaceReader
{
    Task<IReadOnlyList<CaseImportWorkspace>> GetWorkspacesAsync(ActorContext actor, CancellationToken ct);
    Task<IReadOnlyList<CaseImportHistoryItem>> GetRecentAsync(ActorContext actor, string workspaceId, string countryCode, CancellationToken ct);
    Task<IReadOnlyList<CaseImportRowOutcome>> GetIssuesAsync(ActorContext actor, Guid importId, long afterRow, CancellationToken ct);
}
