using System.Collections.Immutable;
using SwiftAid.Application.Authorization;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases.Registration;

/// <summary>Persistence operations scoped to one active registration lease.</summary>
/// <remarks>Stage methods are non-durable; only the lease commit makes their changes durable.</remarks>
public interface ICaseRegistrationPersistence
{
    /// <summary>Looks up only an opaque candidate case id by trusted actor and command key. Call after create-scope authorization.</summary>
    ValueTask<Guid?> FindReceiptCandidateCaseIdAsync(
        ICaseRegistrationLease createLease,
        string actorId,
        Guid commandId,
        CancellationToken cancellationToken);

    /// <summary>Reads the full receipt only after current case replay authorization succeeds.</summary>
    ValueTask<CaseRegistrationReceipt?> FindAuthorizedReceiptAsync(
        ICaseRegistrationLease replayLease,
        string actorId,
        Guid commandId,
        CancellationToken cancellationToken);

    ValueTask<DateTimeOffset> GetDatabaseUtcNowAsync(
        ICaseRegistrationLease createLease,
        CancellationToken cancellationToken);

    ValueTask StageDeniedAuditAsync(
        ICaseRegistrationLease lease,
        CaseRegistrationDenialAudit audit,
        CancellationToken cancellationToken);

    ValueTask<CaseRegistrationAppendOutcome> StageAcceptedRegistrationAsync(
        ICaseRegistrationLease createLease,
        CaseRegistrationAppendRequest request,
        CancellationToken cancellationToken);
}

public sealed record CaseRegistrationReceipt(
    string ActorId,
    Guid CommandId,
    Guid CaseId,
    string CommandType,
    int ReceiptProtocolVersion,
    int WireCommandSchemaVersion,
    ImmutableArray<byte> CanonicalPayloadHash,
    ImmutableArray<Guid> AcceptedEventIds,
    long AggregateVersion,
    long ReportRevision);

public sealed class CaseRegistrationAppendRequest
{
    public CaseRegistrationAppendRequest(
        CanonicalCaseRegistrationSnapshot snapshot,
        string matchedGrantGroupId,
        Guid caseId,
        long accessEpoch,
        DateTimeOffset recordedAtUtc,
        IEnumerable<CaseEvent> events)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        if (string.IsNullOrWhiteSpace(matchedGrantGroupId) ||
            !string.Equals(matchedGrantGroupId, matchedGrantGroupId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical authorized grant group is required.", nameof(matchedGrantGroupId));
        }

        if (caseId == Guid.Empty) throw new ArgumentException("A case identifier is required.", nameof(caseId));
        if (accessEpoch < 0) throw new ArgumentOutOfRangeException(nameof(accessEpoch));
        if (recordedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("The server timestamp must be UTC.", nameof(recordedAtUtc));
        ArgumentNullException.ThrowIfNull(events);
        var eventSnapshot = events.ToImmutableArray();
        if (eventSnapshot.IsDefaultOrEmpty || eventSnapshot.Any(item => item is null))
        {
            throw new ArgumentException("At least one case event is required.", nameof(events));
        }

        if (eventSnapshot.Any(item => item.CaseId != caseId || item.CommandId != snapshot.CommandId ||
                !string.Equals(item.ActorId, snapshot.ActorId, StringComparison.Ordinal) || item.RecordedAtUtc != recordedAtUtc))
        {
            throw new ArgumentException("Staged events must match the immutable registration snapshot and server timestamp.", nameof(events));
        }

        MatchedGrantGroupId = matchedGrantGroupId;
        CaseId = caseId;
        AccessEpoch = accessEpoch;
        RecordedAtUtc = recordedAtUtc;
        Events = eventSnapshot;
    }

    public CanonicalCaseRegistrationSnapshot Snapshot { get; }
    public string MatchedGrantGroupId { get; }
    public Guid CaseId { get; }
    public long AccessEpoch { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public ImmutableArray<CaseEvent> Events { get; }
}

public enum CaseRegistrationAppendStatus
{
    Staged,
    AuthorizationChanged,
    CommandKeyConflict,
    Unavailable
}

public sealed record CaseRegistrationAppendOutcome(
    CaseRegistrationAppendStatus Status,
    ImmutableArray<Guid> EventIds,
    long AggregateVersion,
    long ReportRevision);

/// <summary>Minimized registration denial audit; no narrative or confirmation text is included.</summary>
public sealed record CaseRegistrationDenialAudit(string ActorId, Guid CommandId, AccessDecisionCode DecisionCode);
