using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Contracts.Events;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence.Processing;

namespace SwiftAid.Infrastructure.Persistence.Registration;

/// <summary>Registration-scoped persistence over one active transaction lease.</summary>
/// <remarks>
/// Receipt lookup and denial audit are fully implemented. Event staging
/// (<c>StageAcceptedRegistrationAsync</c>) throws <see cref="CaseRegistrationStagingUnavailableException"/>
/// until the journal-staging slice lands; the processor surfaces that explicitly.
/// </remarks>
public sealed class PostgresCaseRegistrationPersistence : ICaseRegistrationPersistence
{
    private readonly SwiftAidDbContext _dbContext;

    public PostgresCaseRegistrationPersistence(SwiftAidDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async ValueTask<Guid?> FindReceiptCandidateCaseIdAsync(
        ICaseRegistrationLease lease,
        string actorId,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        RequireLease(lease, CaseRegistrationLeaseMode.Create);
        RequireCanonicalActor(actorId);
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("A command identifier is required.", nameof(commandId));
        }

        return await _dbContext.CommandReceipts.AsNoTracking()
            .Where(entity => entity.ActorId == actorId && entity.CommandId == commandId)
            .Select(entity => (Guid?)entity.CaseId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<CaseRegistrationReceipt?> FindAuthorizedReceiptAsync(
        ICaseRegistrationLease replayLease,
        string actorId,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        RequireLease(replayLease, CaseRegistrationLeaseMode.ExistingCaseReplay);
        RequireCanonicalActor(actorId);
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("A command identifier is required.", nameof(commandId));
        }

        var row = await _dbContext.CommandReceipts.AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.ActorId == actorId && entity.CommandId == commandId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || !string.Equals(row.CommandType, CanonicalCaseRegistrationHasher.CommandType, StringComparison.Ordinal))
        {
            return null;
        }

        if (row.SchemaVersion != CanonicalCaseRegistrationHasher.CurrentReceiptProtocolVersion)
        {
            return new CaseRegistrationReceipt(
                row.ActorId, row.CommandId, row.CaseId, row.CommandType,
                row.SchemaVersion, CanonicalCaseRegistrationHasher.CurrentWireCommandSchemaVersion,
                ImmutableArray<byte>.Empty, ImmutableArray<Guid>.Empty,
                row.LastAggregateVersion, row.ReportRevision);
        }

        var storedEvents = await _dbContext.CaseEvents.AsNoTracking()
            .Where(entity => entity.CaseId == row.CaseId && entity.CommandId == row.CommandId)
            .OrderBy(entity => entity.AggregateVersion)
            .Select(entity => new
            {
                entity.EventId,
                entity.ActorId,
                entity.AggregateVersion,
                entity.ReportRevision,
                entity.RecordedAtUtc,
                entity.SchemaVersion,
                entity.EventType
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row.ReceiptId == Guid.Empty || row.CanonicalPayloadHash.Length != 32 ||
            row.FirstAggregateVersion < 1 || row.LastAggregateVersion != 2 ||
            row.ReportRevision != 1 || row.AcceptedAtUtc.Offset != TimeSpan.Zero ||
            storedEvents.Count != 2 ||
            storedEvents[0].AggregateVersion != 1 || storedEvents[1].AggregateVersion != 2 ||
            storedEvents.Any(item => item.EventId == Guid.Empty ||
                !string.Equals(item.ActorId, row.ActorId, StringComparison.Ordinal) ||
                item.SchemaVersion is not (1 or 2) ||
                item.EventType is not (nameof(CaseEventType.CaseReported) or nameof(CaseEventType.CaseAssigned)) ||
                item.ReportRevision != 1) ||
            !CaseEventJournalReader.SamePostgresTimestamp(row.AcceptedAtUtc, storedEvents[^1].RecordedAtUtc))
        {
            throw new InvalidDataException("The stored registration receipt is inconsistent with its case journal.");
        }

        return new CaseRegistrationReceipt(
            row.ActorId,
            row.CommandId,
            row.CaseId,
            row.CommandType,
            row.SchemaVersion,
            CanonicalCaseRegistrationHasher.CurrentWireCommandSchemaVersion,
            ImmutableArray.CreateRange(row.CanonicalPayloadHash),
            storedEvents.Select(item => item.EventId).ToImmutableArray(),
            row.LastAggregateVersion,
            row.ReportRevision);
    }

    public async ValueTask<DateTimeOffset> GetDatabaseUtcNowAsync(
        ICaseRegistrationLease lease,
        CancellationToken cancellationToken)
    {
        var current = RequireAnyLease(lease);
        await using var command = current.DbContext.Database.GetDbConnection().CreateCommand();
        command.Transaction = current.Transaction.GetDbTransaction();
        command.CommandText = "SELECT clock_timestamp() AT TIME ZONE 'UTC'";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value switch
        {
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            _ => throw new InvalidOperationException("The database clock could not be read.")
        };
    }

    public async ValueTask StageDeniedAuditAsync(
        ICaseRegistrationLease lease,
        CaseRegistrationDenialAudit audit,
        CancellationToken cancellationToken)
    {
        var current = RequireAnyLease(lease);
        ArgumentNullException.ThrowIfNull(audit);
        if (string.IsNullOrWhiteSpace(audit.ActorId) ||
            !string.Equals(audit.ActorId, current.PrimaryActorId, StringComparison.Ordinal) ||
            audit.CommandId == Guid.Empty ||
            !Enum.IsDefined(audit.DecisionCode) || audit.DecisionCode == AccessDecisionCode.Allowed)
        {
            throw new ArgumentException("The denial audit does not match the active registration lease.", nameof(audit));
        }

        var accessEpoch = await _dbContext.ActorAccessStates.AsNoTracking()
            .Where(entity => entity.ActorId == audit.ActorId)
            .Select(entity => (long?)entity.AccessEpoch)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (accessEpoch is null)
        {
            throw new InvalidOperationException("The current actor access state is unavailable.");
        }

        var now = await GetDatabaseUtcNowAsync(lease, cancellationToken).ConfigureAwait(false);
        _dbContext.SecurityAuditEntries.Add(new SecurityAuditEntryEntity
        {
            AuditId = Guid.NewGuid(),
            ActorId = audit.ActorId,
            ResourceId = current.CaseId,
            Operation = "CaseRegistration.Denied",
            DecisionCode = audit.DecisionCode.ToString(),
            AccessEpoch = accessEpoch.Value,
            RecordedAtUtc = now
        });
    }

    public ValueTask<CaseRegistrationAppendOutcome> StageAcceptedRegistrationAsync(
        ICaseRegistrationLease createLease,
        CaseRegistrationAppendRequest request,
        CancellationToken cancellationToken)
    {
        // The entire staging operation is unavailable until the journal-staging slice lands;
        // argument validation arrives with the real implementation.
        throw new CaseRegistrationStagingUnavailableException();
    }

    private PostgresCaseRegistrationLease RequireLease(ICaseRegistrationLease lease, CaseRegistrationLeaseMode mode)
    {
        var current = RequireAnyLease(lease);
        if (current.Mode != mode)
        {
            throw new ArgumentException("The registration lease mode does not support this operation.", nameof(lease));
        }

        return current;
    }

    private static PostgresCaseRegistrationLease RequireAnyLease(ICaseRegistrationLease lease)
    {
        if (lease is not PostgresCaseRegistrationLease current || !current.IsActive)
        {
            throw new ArgumentException("An active registration lease is required.", nameof(lease));
        }

        return current;
    }

    private static void RequireCanonicalActor(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId) || !string.Equals(actorId, actorId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical actor identifier is required.", nameof(actorId));
        }
    }
}
