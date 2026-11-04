namespace SwiftAid.Contracts.Cases;

/// <summary>Minimum-necessary queue row for one officer-owned managed case.</summary>
/// <remarks>
/// No report content, narratives, or sealed payloads: the queue carries routing
/// identity only. Full history stays on the per-case timeline behind ReadCase.
/// </remarks>
public sealed record CaseQueueItem(
    Guid CaseId,
    string Workflow,
    string CountryCode,
    string AccountableOwnerActorId,
    long AggregateVersion,
    DateTimeOffset UpdatedAtUtc,
    string? Sector = null,
    string? Classification = null,
    string? WorkspaceId = null,
    DateTimeOffset? CreatedAtUtc = null,
    bool HasReportJournal = true);

/// <summary>Queue read outcome distinguishing denial/unavailability from an empty queue.</summary>
public enum CaseQueueAccess
{
    Allowed,
    Denied,
    Unavailable
}

/// <summary>One page of the officer's authorized queue.</summary>
/// <remarks><see cref="NextCursor"/> is a position only; every subsequent row is authorized again by the server.</remarks>
public sealed record CaseQueueResult(
    CaseQueueAccess Access,
    IReadOnlyList<CaseQueueItem> Items,
    string? NextCursor = null);

/// <summary>Staff "my cases" client. Servers enforce ReadCase per case; clients only render.</summary>
public interface ICaseQueueClient
{
    Task<CaseQueueResult> GetMineAsync(int? limit, CancellationToken cancellationToken = default);

    Task<CaseQueueResult> GetMineAsync(int? limit, string? cursor, CancellationToken cancellationToken = default) =>
        cursor is null
            ? GetMineAsync(limit, cancellationToken)
            : Task.FromResult(new CaseQueueResult(CaseQueueAccess.Unavailable, []));
}
