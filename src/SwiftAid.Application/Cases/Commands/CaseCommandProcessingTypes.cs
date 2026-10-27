using System.Collections.Immutable;
using SwiftAid.Application.Authorization;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases.Commands;

/// <summary>Current non-sensitive projection values needed to validate a command.</summary>
public sealed record CaseCommandCaseHeader(
    Guid CaseId,
    long AggregateVersion,
    long ReportRevision,
    long ResourceVersion,
    string AccountableOwnerActorId);

/// <summary>Persisted acceptance data associated with one actor and command key.</summary>
public sealed record CaseCommandReceipt(
    string ActorId,
    Guid CommandId,
    Guid CaseId,
    string CommandType,
    int SchemaVersion,
    ImmutableArray<byte> CanonicalPayloadHash,
    ImmutableArray<Guid> AcceptedEventIds,
    long AggregateVersion,
    long ReportRevision);

/// <summary>Server-derived authority used again by the final persistence guard.</summary>
public sealed record CaseCommandAuthorizationSnapshot(
    string ActorId,
    long AccessEpoch,
    string MatchedGrantGroupId,
    ImmutableArray<CaseOperation> RequiredOperations,
    long ExpectedResourceVersion);

/// <summary>Immutable inputs for staging one accepted command in its active write lease.</summary>
public sealed class CaseCommandAppendRequest
{
    public CaseCommandAppendRequest(
        CanonicalCaseCommandSnapshot commandSnapshot,
        CaseCommandAuthorizationSnapshot authorization,
        IEnumerable<CaseEvent> events,
        long expectedAggregateVersion,
        long expectedReportRevision,
        long expectedResourceVersion,
        string? assignmentTargetActorId)
    {
        CommandSnapshot = commandSnapshot ?? throw new ArgumentNullException(nameof(commandSnapshot));
        Authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        ArgumentNullException.ThrowIfNull(events);

        var eventSnapshot = events.ToImmutableArray();
        if (eventSnapshot.IsDefaultOrEmpty || eventSnapshot.Any(@event => @event is null))
        {
            throw new ArgumentException("At least one non-null case event is required.", nameof(events));
        }

        Events = eventSnapshot;
        ExpectedAggregateVersion = expectedAggregateVersion;
        ExpectedReportRevision = expectedReportRevision;
        ExpectedResourceVersion = expectedResourceVersion;
        AssignmentTargetActorId = assignmentTargetActorId;
    }

    public CanonicalCaseCommandSnapshot CommandSnapshot { get; }

    public CaseCommandAuthorizationSnapshot Authorization { get; }

    public ImmutableArray<CaseEvent> Events { get; }

    public long ExpectedAggregateVersion { get; }

    public long ExpectedReportRevision { get; }

    public long ExpectedResourceVersion { get; }

    public string? AssignmentTargetActorId { get; }
}

public enum CaseCommandAppendStatus
{
    Staged,
    CaseVersionConflict,
    AuthorizationChanged,
    AssignmentTargetDenied
}

/// <summary>Outcome of staging an accepted command; Staged is not durable until lease commit.</summary>
public sealed record CaseCommandAppendOutcome(
    CaseCommandAppendStatus Status,
    ImmutableArray<Guid> EventIds,
    long AggregateVersion,
    long ReportRevision)
{
    public static CaseCommandAppendOutcome Staged(
        IEnumerable<Guid> eventIds,
        long aggregateVersion,
        long reportRevision)
    {
        ArgumentNullException.ThrowIfNull(eventIds);
        var snapshot = eventIds.ToImmutableArray();
        if (snapshot.IsDefaultOrEmpty || snapshot.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException("A staged command must contain event identifiers.", nameof(eventIds));
        }

        return new CaseCommandAppendOutcome(CaseCommandAppendStatus.Staged, snapshot, aggregateVersion, reportRevision);
    }
}

/// <summary>Safe minimized audit fields for a denied managed-case command.</summary>
public sealed record CaseCommandDenialAudit(
    string ActorId,
    Guid CaseId,
    Guid CommandId,
    AccessDecisionCode DecisionCode);

public enum CaseCommandProcessingStatus
{
    Accepted,
    Replayed,
    Conflict,
    Denied,
    Invalid,
    Unavailable
}

/// <summary>Safe command result. It contains no payload, grant group, hash, or internal exception.</summary>
public sealed record CaseCommandProcessingResult(
    CaseCommandProcessingStatus Status,
    Guid CaseId,
    Guid CommandId,
    ImmutableArray<Guid> EventIds,
    long? AggregateVersion,
    long? ReportRevision,
    bool DenialAuditDurable = false);
