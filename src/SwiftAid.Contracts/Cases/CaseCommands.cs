namespace SwiftAid.Contracts.Cases;

/// <summary>
/// Staff case-command input matching the version-one command wire fields.
/// Only routing fields travel here; authorization always derives from the
/// server-authenticated principal.
/// </summary>
/// <param name="CommandType">One of CorrectReport, RequestProtectedAction, ReviewProtectedAction, Resolve, Reopen.</param>
/// <param name="Action">Protected-action name (for example Reassign); required by RequestProtectedAction.</param>
/// <param name="Reason">Operator reason; required by every command type.</param>
/// <param name="ProposedOwnerId">Reassignment target, reopened-case owner, or accepted owner.</param>
/// <param name="RequestId">Protected-action request key.</param>
/// <param name="Outcome">Review outcome name (Pending, Denied, Approved); required by ReviewProtectedAction.</param>
/// <param name="ExpectedVersion">Optional caller concurrency hint. Servers replace it with the
/// authoritative timeline header version before dispatch.</param>
public sealed record CaseCommandInput(
    string CommandType,
    string? Action,
    string? Reason,
    string? ProposedOwnerId,
    Guid? RequestId,
    string? Outcome,
    long? ExpectedVersion);

/// <summary>Staff case-command outcome. Denial and absence share one value for privacy.</summary>
public enum CaseCommandStatus
{
    Accepted,
    Replayed,
    Conflict,
    Denied,
    Invalid,
    Unavailable
}

/// <summary>Safe command result. It contains no payload, grant group, or internal exception.</summary>
public sealed record CaseCommandResult(
    CaseCommandStatus Status,
    Guid CommandId,
    IReadOnlyList<Guid> EventIds,
    long? AggregateVersion,
    long? ReportRevision);

/// <summary>Staff case-command client. Servers enforce authorization; clients only render.</summary>
public interface ICaseCommandClient
{
    Task<CaseCommandResult> SendAsync(Guid caseId, CaseCommandInput input, CancellationToken cancellationToken = default);
}
