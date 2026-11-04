using System.Collections.Immutable;

namespace SwiftAid.Application.Cases.Registration;

public enum CaseRegistrationStatus
{
    Accepted,
    Replayed,
    Conflict,
    Denied,
    Invalid,
    Unavailable
}

/// <summary>Safe registration outcome; it never exposes payloads, grant groups, hashes, or exception details.</summary>
public sealed record CaseRegistrationResult(
    CaseRegistrationStatus Status,
    Guid? CaseId,
    Guid CommandId,
    ImmutableArray<Guid> EventIds,
    long? AggregateVersion,
    long? ReportRevision,
    bool DenialAuditDurable = false)
{
    public static CaseRegistrationResult WithoutCase(
        CaseRegistrationStatus status,
        Guid commandId,
        bool denialAuditDurable = false) =>
        new(status, null, commandId, ImmutableArray<Guid>.Empty, null, null, denialAuditDurable);
}
