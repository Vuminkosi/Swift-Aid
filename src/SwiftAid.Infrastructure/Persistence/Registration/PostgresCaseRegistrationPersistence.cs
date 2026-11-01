using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Events;
using SwiftAid.Domain.Cases;
using SwiftAid.Domain.Notifications;
using SwiftAid.Infrastructure.Persistence.Notifications;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;

namespace SwiftAid.Infrastructure.Persistence.Registration;

/// <summary>Registration-scoped persistence over one active transaction lease.</summary>
/// <remarks>
/// Staging inserts the new managed-case projection, stages both protected event payloads,
/// appends the two journal rows with domain-outbox entries, records the idempotency receipt,
/// notifies the accountable owner, and writes the acceptance audit row, all inside the
/// create-lease transaction. The matched grant group is re-verified here because the
/// authorization decision was made earlier in the same transaction; a concurrent grant,
/// membership, organization, workspace, or epoch change surfaces as
/// <see cref="CaseRegistrationAppendStatus.AuthorizationChanged"/> instead of staging
/// under revoked authority. The new case inherits its sensitivity code from that
/// re-verified grant group: registration reports carry no sensitivity claim, so the
/// authorizing scope is the only grounded source.
/// </remarks>
public sealed class PostgresCaseRegistrationPersistence : ICaseRegistrationPersistence
{
    private readonly SwiftAidDbContext _dbContext;
    private readonly PostgresProtectedCaseEventPayloadStore _payloadStore;
    private readonly CaseEventSerializer _eventSerializer;

    public PostgresCaseRegistrationPersistence(
        SwiftAidDbContext dbContext,
        PostgresProtectedCaseEventPayloadStore payloadStore,
        CaseEventSerializer eventSerializer)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _eventSerializer = eventSerializer ?? throw new ArgumentNullException(nameof(eventSerializer));
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

    public async ValueTask<CaseRegistrationAppendOutcome> StageAcceptedRegistrationAsync(
        ICaseRegistrationLease createLease,
        CaseRegistrationAppendRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await StageAcceptedRegistrationCoreAsync(createLease, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (PostgresCaseCommandErrorMapper.TryMap(exception, out var mapped))
        {
            if (mapped!.Kind == CasePersistenceRaceKind.ReceiptKeyConflict)
            {
                return new CaseRegistrationAppendOutcome(
                    CaseRegistrationAppendStatus.CommandKeyConflict, ImmutableArray<Guid>.Empty, 0, 0);
            }

            throw mapped;
        }
        catch (DbUpdateException dbUpdate) when (
            dbUpdate.InnerException is PostgresException postgres &&
            postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
            string.Equals(postgres.ConstraintName, "pk_managed_cases", StringComparison.Ordinal))
        {
            // A freshly generated case identifier collided with a concurrently registered
            // case; the processor retries the whole registration with a new identifier.
            return new CaseRegistrationAppendOutcome(
                CaseRegistrationAppendStatus.CommandKeyConflict, ImmutableArray<Guid>.Empty, 0, 0);
        }
    }

    private async ValueTask<CaseRegistrationAppendOutcome> StageAcceptedRegistrationCoreAsync(
        ICaseRegistrationLease createLease,
        CaseRegistrationAppendRequest request,
        CancellationToken cancellationToken)
    {
        var current = RequireLease(createLease, CaseRegistrationLeaseMode.Create);
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = request.Snapshot;
        RequireCanonicalActor(snapshot.ActorId);
        if (!string.Equals(snapshot.ActorId, current.PrimaryActorId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The registration snapshot does not belong to the active lease actor.", nameof(request));
        }

        if (!string.Equals(snapshot.WorkspaceId, current.WorkspaceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The registration snapshot workspace does not match the active lease.", nameof(request));
        }

        if (snapshot.CanonicalPayloadHash.IsDefaultOrEmpty || snapshot.CanonicalPayloadHash.Length != 32)
        {
            throw new ArgumentException("The registration snapshot hash is invalid.", nameof(request));
        }

        if (request.Events.Length != 2 ||
            request.Events[0] is not CaseReported reported ||
            request.Events[1] is not CaseAssigned assigned)
        {
            throw new ArgumentException("Registration staging requires exactly one report event followed by its assignment.", nameof(request));
        }

        if (reported.AggregateVersion != 1 || reported.ReportRevision != 1 ||
            assigned.AggregateVersion != 2 || assigned.ReportRevision != 1)
        {
            throw new ArgumentException("The staged registration versions are invalid.", nameof(request));
        }

        var eventIds = request.Events.Select(item => item.EventId).ToImmutableArray();
        if (eventIds.Any(id => id == Guid.Empty) || eventIds.Distinct().Count() != eventIds.Length)
        {
            throw new ArgumentException("The staged registration events require distinct identifiers.", nameof(request));
        }

        if (!string.Equals(reported.WorkspaceId, current.WorkspaceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The staged report workspace does not match the active lease.", nameof(request));
        }

        RequireCanonicalActor(assigned.AccountableOwnerId);
        var (countryCode, sector, kind) = reported.ReportData switch
        {
            CaseReportEventData.HumanV2 human => (human.Confirmations.OccurrenceCountryCode, Sector.HumanHealth, CaseKind.Human),
            CaseReportEventData.AnimalV2 animal => (animal.Confirmations.OccurrenceCountryCode, Sector.AnimalHealth, CaseKind.Animal),
            _ => throw new ArgumentException("Only versioned human or animal reports can be staged.", nameof(request)),
        };
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            throw new ArgumentException("The staged report country is invalid.", nameof(request));
        }

        var grant = await ReverifyGrantAsync(current, request, snapshot, countryCode, sector, cancellationToken)
            .ConfigureAwait(false);
        if (grant is null || string.IsNullOrWhiteSpace(grant.SensitivityCode))
        {
            return new CaseRegistrationAppendOutcome(
                CaseRegistrationAppendStatus.AuthorizationChanged, ImmutableArray<Guid>.Empty, 0, 0);
        }

        var now = request.RecordedAtUtc;
        _dbContext.ManagedCases.Add(new ManagedCaseEntity
        {
            CaseId = request.CaseId,
            OrganizationId = grant.OrganizationId,
            WorkspaceId = current.WorkspaceId,
            CountryCode = countryCode,
            Kind = kind,
            Sector = sector,
            SensitivityCode = grant.SensitivityCode,
            AccountableOwnerActorId = assigned.AccountableOwnerId,
            ResourceVersion = 1,
            AggregateVersion = 2,
            ReportRevision = 1,
            Workflow = CaseWorkflow.Reported,
            WorkflowVersion = 0,
            Classification = ClinicalClassification.Unclassified,
            ClassificationVersion = 0,
            Laboratory = LaboratoryState.NotRecorded,
            LaboratoryVersion = 0,
            OutcomeState = ReportedValueState.Unknown,
            OutcomeValue = null,
            OutcomeVersion = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

        var preparedEvents = new List<PreparedRegistrationEvent>(request.Events.Length);
        foreach (var @event in request.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eventType = @event.GetType().Name;
            var serialized = _eventSerializer.SerializeToUtf8Bytes(@event);
            try
            {
                var payloadId = Guid.NewGuid();
                var eventSchemaVersion = _eventSerializer.GetSchemaVersion(@event);
                var reference = await _payloadStore.StageProtectedForRegistrationAsync(
                    current,
                    request.CaseId,
                    payloadId,
                    eventType,
                    eventSchemaVersion,
                    serialized,
                    cancellationToken).ConfigureAwait(false);
                if (reference.PayloadId != payloadId || string.IsNullOrWhiteSpace(reference.ProtectionPurposeVersion))
                {
                    throw new ProtectedCaseEventPayloadException();
                }

                preparedEvents.Add(new PreparedRegistrationEvent(@event, eventType, eventSchemaVersion, reference));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serialized);
            }
        }

        _dbContext.CommandReceipts.Add(new CommandReceiptEntity
        {
            ReceiptId = Guid.NewGuid(),
            ActorId = snapshot.ActorId,
            CommandId = snapshot.CommandId,
            CaseId = request.CaseId,
            CommandType = CanonicalCaseRegistrationHasher.CommandType,
            SchemaVersion = snapshot.ReceiptProtocolVersion,
            CanonicalPayloadHash = snapshot.CanonicalPayloadHash.ToArray(),
            FirstAggregateVersion = 1,
            LastAggregateVersion = 2,
            ReportRevision = 1,
            AcceptedAtUtc = now
        });

        foreach (var prepared in preparedEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var @event = prepared.Event;
            _dbContext.CaseEvents.Add(new CaseEventEntity
            {
                EventId = @event.EventId,
                CaseId = @event.CaseId,
                AggregateVersion = @event.AggregateVersion,
                ReportRevision = @event.ReportRevision,
                CommandId = @event.CommandId,
                ActorId = @event.ActorId,
                EventType = prepared.EventType,
                SchemaVersion = prepared.SchemaVersion,
                OccurredAtUtc = @event.OccurredAtUtc,
                RecordedAtUtc = @event.RecordedAtUtc,
                PayloadId = prepared.PayloadReference.PayloadId
            });
            _dbContext.DomainOutbox.Add(new DomainOutboxEntity
            {
                OutboxId = Guid.NewGuid(),
                EventId = @event.EventId,
                MessageType = prepared.EventType,
                CreatedAtUtc = @event.RecordedAtUtc,
                AvailableAtUtc = @event.RecordedAtUtc,
                AttemptCount = 0
            });
        }

        var occurrenceKey = $"{assigned.EventId:N}:{assigned.AccountableOwnerId}";
        var notification = NotificationItem.Create(
            assigned.EventId,
            occurrenceKey,
            assigned.AccountableOwnerId,
            NotificationKind.CaseNeedsAttention,
            NotificationResourceKind.ManagedCase,
            request.CaseId,
            NotificationAction.OpenCase,
            NotificationRecipientRequirement.CurrentAccountableOwner,
            assigned.RecordedAtUtc);
        _dbContext.NotificationItems.Add(notification);
        _dbContext.NotificationDeliveryOutbox.Add(new NotificationDeliveryOutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            NotificationId = notification.NotificationId,
            CreatedAtUtc = assigned.RecordedAtUtc,
            AvailableAtUtc = assigned.RecordedAtUtc
        });

        _dbContext.SecurityAuditEntries.Add(new SecurityAuditEntryEntity
        {
            AuditId = Guid.NewGuid(),
            ActorId = snapshot.ActorId,
            TargetActorId = assigned.AccountableOwnerId,
            ResourceId = request.CaseId,
            Operation = $"CaseRegistration.{CanonicalCaseRegistrationHasher.CommandType}",
            DecisionCode = AccessDecisionCode.Allowed.ToString(),
            AccessEpoch = request.AccessEpoch,
            RecordedAtUtc = now
        });

        // Flush inside the lease transaction so a restaged command key raises its
        // receipt/case uniqueness violation here, where the caller maps it to
        // CommandKeyConflict, rather than surfacing only at lease commit.
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CaseRegistrationAppendOutcome(
            CaseRegistrationAppendStatus.Staged, eventIds, 2, 1);
    }

    private async ValueTask<ReverifiedGrant?> ReverifyGrantAsync(
        PostgresCaseRegistrationLease current,
        CaseRegistrationAppendRequest request,
        CanonicalCaseRegistrationSnapshot snapshot,
        string countryCode,
        Sector sector,
        CancellationToken cancellationToken)
    {
        // Statement-level re-verification inside the create-lease transaction: under read
        // committed isolation this observes grants, memberships, and epochs committed after
        // the earlier authorization decision, closing the revocation race.
        var match = await (
            from item in _dbContext.AccessGrantGroups.AsNoTracking()
            join organization in _dbContext.Organizations.AsNoTracking()
                on item.OrganizationId equals organization.OrganizationId
            join workspace in _dbContext.Workspaces.AsNoTracking()
                on item.WorkspaceId equals workspace.WorkspaceId
            join member in _dbContext.OrganizationMemberships.AsNoTracking()
                on item.MembershipId equals member.MembershipId
            join access in _dbContext.ActorAccessStates.AsNoTracking()
                on item.ActorId equals access.ActorId
            where item.GrantGroupId == request.MatchedGrantGroupId &&
                item.ActorId == snapshot.ActorId &&
                member.ActorId == snapshot.ActorId &&
                item.OrganizationId == member.OrganizationId &&
                (snapshot.OrganizationId == null || item.OrganizationId == snapshot.OrganizationId) &&
                member.Status == PersistentMembershipStatus.Active &&
                member.ValidFromUtc <= request.RecordedAtUtc &&
                (member.ExpiresAtUtc == null || member.ExpiresAtUtc > request.RecordedAtUtc) &&
                item.WorkspaceId == current.WorkspaceId &&
                item.Status == PersistentGrantStatus.Active &&
                item.ValidFromUtc <= request.RecordedAtUtc &&
                item.ExpiresAtUtc > request.RecordedAtUtc &&
                item.CountryCode == countryCode &&
                item.Sector == sector &&
                organization.Status == PersistentResourceStatus.Active &&
                workspace.Status == PersistentResourceStatus.Active &&
                _dbContext.WorkspaceOrganizations.Any(link => link.WorkspaceId == item.WorkspaceId &&
                    link.OrganizationId == item.OrganizationId && link.Status == PersistentResourceStatus.Active) &&
                access.AccessEpoch == request.AccessEpoch &&
                _dbContext.AccessGrantOperations.AsNoTracking().Any(operation =>
                    operation.GrantGroupId == item.GrantGroupId &&
                    operation.Operation == CaseOperation.ReadCase)
            select new ReverifiedGrant(item.OrganizationId, item.SensitivityCode))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return match;
    }

    private sealed record ReverifiedGrant(string OrganizationId, string SensitivityCode);

    private sealed record PreparedRegistrationEvent(
        CaseEvent Event,
        string EventType,
        int SchemaVersion,
        ProtectedCaseEventPayloadReference PayloadReference);

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
