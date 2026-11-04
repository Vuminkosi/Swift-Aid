using System.Collections.Immutable;
using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Events;
using SwiftAid.Domain.Cases;
using SwiftAid.Domain.Notifications;
using SwiftAid.Infrastructure.Persistence.Entities.Cases;
using SwiftAid.Infrastructure.Persistence.Notifications;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Infrastructure.Persistence.Processing;

/// <summary>Persists managed-case command journals and projections inside a case-bound transaction.</summary>
public sealed partial class PostgresCaseCommandPersistence : ICaseCommandPersistence
{
    private const string AllowedOperationsSql = """
        NOT EXISTS (
            SELECT 1
            FROM unnest(@required_operations::text[]) AS requested(operation)
            WHERE NOT EXISTS (
                SELECT 1 FROM public.access_grant_group_operations AS granted
                WHERE granted.grant_group_id = authority.grant_group_id
                  AND granted.operation = requested.operation))
        """;

    private const string CaseRelationshipSql = """
        (mc.accountable_owner_actor_id = @actor_id
         OR EXISTS (
             SELECT 1 FROM public.case_shares AS share
             WHERE share.case_id = mc.case_id
               AND share.actor_id = @actor_id
               AND share.is_active
               AND share.granted_at_utc <= clock_timestamp()
               AND (share.allowed_operations IS NULL OR @required_operations::text[] <@ share.allowed_operations)
               AND (share.expires_at_utc IS NULL OR share.expires_at_utc > clock_timestamp()))
         OR (authority.can_access_jurisdictional_cases
             AND EXISTS (SELECT 1 FROM public.managed_case_regions AS any_region WHERE any_region.case_id = mc.case_id)
             AND NOT EXISTS (
                 SELECT 1 FROM public.managed_case_regions AS region
                 WHERE region.case_id = mc.case_id
                   AND NOT EXISTS (
                       SELECT 1 FROM public.access_grant_group_regions AS grant_region
                       WHERE grant_region.grant_group_id = authority.grant_group_id
                         AND grant_region.region_id = region.region_id))))
        """;

    private readonly SwiftAidDbContext _dbContext;
    private readonly ICaseCommandLeaseAccessor _leaseAccessor;
    private readonly IProtectedCaseEventPayloadStore _payloadStore;
    private readonly CaseEventSerializer _eventSerializer;

    public PostgresCaseCommandPersistence(
        SwiftAidDbContext dbContext,
        ICaseCommandLeaseAccessor leaseAccessor,
        IProtectedCaseEventPayloadStore payloadStore,
        CaseEventSerializer eventSerializer)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _leaseAccessor = leaseAccessor ?? throw new ArgumentNullException(nameof(leaseAccessor));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _eventSerializer = eventSerializer ?? throw new ArgumentNullException(nameof(eventSerializer));
    }

    public async ValueTask<CaseCommandCaseHeader?> ReadCaseHeaderAsync(
        ICaseCommandLease lease,
        CancellationToken cancellationToken)
    {
        var current = RequireLease(lease, CaseLeaseMode.Read, CaseLeaseMode.Write);
        return await _dbContext.ManagedCases.AsNoTracking()
            .Where(entity => entity.CaseId == current.CaseId)
            .Select(entity => new CaseCommandCaseHeader(
                entity.CaseId,
                entity.AggregateVersion,
                entity.ReportRevision,
                entity.ResourceVersion,
                entity.AccountableOwnerActorId))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<CaseAggregate> ReplayCaseAsync(
        ICaseCommandLease lease,
        CancellationToken cancellationToken)
    {
        var current = RequireLease(lease, CaseLeaseMode.Read, CaseLeaseMode.Write);
        var rows = await _dbContext.CaseEvents.AsNoTracking()
            .Where(entity => entity.CaseId == current.CaseId)
            .OrderBy(entity => entity.AggregateVersion)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw InvalidJournal();
        }

        var events = new List<CaseEvent>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.SchemaVersion is not (1 or 2) || row.PayloadId == Guid.Empty)
            {
                throw InvalidJournal();
            }

            var payload = await _dbContext.ProtectedEventPayloads.AsNoTracking()
                .SingleOrDefaultAsync(entity => entity.PayloadId == row.PayloadId, cancellationToken)
                .ConfigureAwait(false);
            if (payload is null || string.IsNullOrWhiteSpace(payload.ProtectionPurposeVersion))
            {
                throw InvalidJournal();
            }

            var plaintext = await _payloadStore.UnprotectAsync(
                row.CaseId,
                row.PayloadId,
                row.EventType,
                row.SchemaVersion,
                payload.ProtectionPurposeVersion,
                payload.ProtectedBytes,
                cancellationToken).ConfigureAwait(false);
            try
            {
                events.Add(CaseEventJournalReader.Read(row, plaintext));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        try
        {
            var aggregate = CaseAggregate.Replay(events);
            var projection = await _dbContext.ManagedCases.AsNoTracking()
                .Where(entity => entity.CaseId == current.CaseId)
                .Select(entity => new
                {
                    entity.AggregateVersion,
                    entity.ReportRevision,
                    entity.Workflow,
                    entity.WorkflowVersion,
                    entity.AccountableOwnerActorId,
                    entity.WorkspaceId
                })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (projection is null || projection.AggregateVersion != aggregate.AggregateVersion ||
                projection.ReportRevision != aggregate.ReportRevision || projection.Workflow != aggregate.Workflow ||
                projection.WorkflowVersion != aggregate.WorkflowVersion ||
                !string.Equals(projection.AccountableOwnerActorId, aggregate.AccountableOwnerId, StringComparison.Ordinal) ||
                !string.Equals(projection.WorkspaceId, aggregate.WorkspaceId, StringComparison.Ordinal))
            {
                throw InvalidJournal();
            }

            return aggregate;
        }
        catch (InvalidDataException)
        {
            throw InvalidJournal();
        }
    }

    public async ValueTask<CaseCommandReceipt?> FindReceiptAsync(
        ICaseCommandLease lease,
        string actorId,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var current = RequireLease(lease, CaseLeaseMode.Read, CaseLeaseMode.Write);
        RequireCanonicalActor(actorId);
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("A command identifier is required.", nameof(commandId));
        }

        var row = await _dbContext.CommandReceipts.AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.ActorId == actorId && entity.CommandId == commandId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        if (row.SchemaVersion != CanonicalCaseCommandHasher.CurrentReceiptProtocolVersion)
        {
            return new CaseCommandReceipt(row.ActorId, row.CommandId, row.CaseId, row.CommandType,
                row.SchemaVersion, ImmutableArray<byte>.Empty, ImmutableArray<Guid>.Empty,
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
            row.FirstAggregateVersion < 1 || row.LastAggregateVersion < row.FirstAggregateVersion ||
            row.ReportRevision < 1 || row.AcceptedAtUtc.Offset != TimeSpan.Zero ||
            storedEvents.Count == 0 || storedEvents.Any(item =>
                (item.SchemaVersion == 2 && item.EventType is not (nameof(CaseEventType.CaseReported) or nameof(CaseEventType.CaseReportCorrected))) ||
                item.SchemaVersion is not (1 or 2) || item.EventId == Guid.Empty ||
                !string.Equals(item.ActorId, row.ActorId, StringComparison.Ordinal)) ||
            storedEvents.Count != row.LastAggregateVersion - row.FirstAggregateVersion + 1 ||
            storedEvents[0].AggregateVersion != row.FirstAggregateVersion ||
            storedEvents[^1].AggregateVersion != row.LastAggregateVersion ||
            storedEvents[^1].ReportRevision != row.ReportRevision ||
            !CaseEventJournalReader.SamePostgresTimestamp(row.AcceptedAtUtc, storedEvents[^1].RecordedAtUtc) ||
            storedEvents.Any(item => item.AggregateVersion < row.FirstAggregateVersion || item.AggregateVersion > row.LastAggregateVersion))
        {
            throw InvalidReceipt();
        }

        return new CaseCommandReceipt(
            row.ActorId,
            row.CommandId,
            row.CaseId,
            row.CommandType,
            row.SchemaVersion,
            ImmutableArray.CreateRange(row.CanonicalPayloadHash),
            storedEvents.Select(item => item.EventId).ToImmutableArray(),
            row.LastAggregateVersion,
            row.ReportRevision);
    }

    public async ValueTask<DateTimeOffset> GetDatabaseUtcNowAsync(
        ICaseCommandLease lease,
        CancellationToken cancellationToken)
    {
        var current = RequireLease(lease, CaseLeaseMode.Read, CaseLeaseMode.Write);
        await using var command = CreateCommand(current, "SELECT clock_timestamp() AT TIME ZONE 'UTC'");
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value switch
        {
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            _ => throw new InvalidOperationException("The database clock could not be read.")
        };
    }

    public ValueTask<bool> IsEligibleAssignmentTargetAsync(
        ICaseCommandLease lease,
        string targetActorId,
        CaseCommandCaseHeader caseHeader,
        CancellationToken cancellationToken) =>
        IsEligibleTargetAsync(lease, targetActorId, [nameof(CaseOperation.ReadCase)], caseHeader, cancellationToken);

    public ValueTask<bool> IsEligibleFollowUpAssigneeAsync(
        ICaseCommandLease lease,
        string targetActorId,
        CaseCommandCaseHeader caseHeader,
        CancellationToken cancellationToken) =>
        IsEligibleTargetAsync(lease, targetActorId,
            [nameof(CaseOperation.ReadCase), nameof(CaseOperation.ManageFollowUp)], caseHeader, cancellationToken);

    public ValueTask<bool> IsEligibleCollaboratorAsync(
        ICaseCommandLease lease,
        string targetActorId,
        CaseCollaborationActions actions,
        CaseCommandCaseHeader caseHeader,
        CancellationToken cancellationToken)
    {
        const CaseCollaborationActions supported = CaseCollaborationActions.ReadCase | CaseCollaborationActions.ReadAttachment |
            CaseCollaborationActions.Synchronize | CaseCollaborationActions.RecordObservation |
            CaseCollaborationActions.ManageFollowUp | CaseCollaborationActions.ManageEscalation;
        if (!actions.HasFlag(CaseCollaborationActions.ReadCase) || (actions & ~supported) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actions));
        }

        return IsEligibleTargetAsync(lease, targetActorId,
            Enum.GetValues<CaseCollaborationActions>().Where(action => actions.HasFlag(action))
                .Select(action => action.ToString()).ToArray(), caseHeader, cancellationToken);
    }

    private async ValueTask<bool> IsEligibleTargetAsync(
        ICaseCommandLease lease,
        string targetActorId,
        string[] requiredOperations,
        CaseCommandCaseHeader caseHeader,
        CancellationToken cancellationToken)
    {
        var current = RequireLease(lease, CaseLeaseMode.Write);
        RequireCanonicalActor(targetActorId);
        if (!current.LockedActorIds.Contains(targetActorId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The target actor is not locked by the active case lease.");
        }
        ArgumentNullException.ThrowIfNull(caseHeader);
        if (caseHeader.CaseId != current.CaseId)
        {
            throw new InvalidOperationException("The case header does not match the active case lease.");
        }

        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM public.managed_cases AS mc
                JOIN public.organizations AS org ON org.organization_id = mc.organization_id
                JOIN public.workspaces AS ws ON ws.workspace_id = mc.workspace_id
                JOIN public.workspace_organizations AS workspace_link ON workspace_link.workspace_id = mc.workspace_id
                  AND workspace_link.organization_id = mc.organization_id AND workspace_link.status = 'Active'
                JOIN public.actors AS actor ON actor.actor_id = @target_actor_id
                JOIN public.actor_access_states AS access_state ON access_state.actor_id = actor.actor_id
                JOIN public.organization_memberships AS member
                  ON member.actor_id = actor.actor_id AND member.organization_id = mc.organization_id
                JOIN public.access_grant_groups AS grant_group
                  ON grant_group.membership_id = member.membership_id
                 AND grant_group.actor_id = actor.actor_id
                 AND grant_group.organization_id = mc.organization_id
                 AND grant_group.workspace_id = mc.workspace_id
                 AND grant_group.country_code = mc.country_code
                 AND grant_group.sector = mc.sector::text
                 AND grant_group.sensitivity_code = mc.sensitivity_code
                JOIN public.access_grant_group_operations AS read_operation
                  ON read_operation.grant_group_id = grant_group.grant_group_id
                 AND read_operation.operation = 'ReadCase'
                WHERE mc.case_id = @case_id
                  AND mc.resource_version = @resource_version
                  AND org.status = 'Active' AND ws.status = 'Active'
                  AND actor.status = 'Active'
                  AND (actor.expires_at_utc IS NULL OR actor.expires_at_utc > clock_timestamp())
                  AND member.status = 'Active'
                  AND member.valid_from_utc <= clock_timestamp()
                  AND (member.expires_at_utc IS NULL OR member.expires_at_utc > clock_timestamp())
                  AND grant_group.status = 'Active'
                  AND grant_group.valid_from_utc <= clock_timestamp()
                  AND grant_group.expires_at_utc > clock_timestamp()
                  AND NOT EXISTS (
                      SELECT 1 FROM unnest(@required_operations::text[]) AS required(operation)
                      WHERE NOT EXISTS (
                          SELECT 1 FROM public.access_grant_group_operations AS permitted
                          WHERE permitted.grant_group_id = grant_group.grant_group_id
                            AND permitted.operation = required.operation)))
            """;

        await using var command = CreateCommand(current, sql,
            ("target_actor_id", targetActorId),
            ("case_id", current.CaseId),
            ("required_operations", requiredOperations),
            ("resource_version", caseHeader.ResourceVersion));
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The assignment target check returned no result."));
    }

    public async ValueTask<CaseCommandAppendOutcome> StageAcceptedCommandAsync(
        ICaseCommandLease lease,
        CaseCommandAppendRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await StageAcceptedCommandCoreAsync(lease, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (PostgresCaseCommandErrorMapper.TryMap(exception, out _))
        {
            _ = PostgresCaseCommandErrorMapper.TryMap(exception, out var mapped);
            throw mapped!;
        }
    }

    private async ValueTask<CaseCommandAppendOutcome> StageAcceptedCommandCoreAsync(
        ICaseCommandLease lease,
        CaseCommandAppendRequest request,
        CancellationToken cancellationToken)
    {
        var current = RequireLease(lease, CaseLeaseMode.Write);
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = request.CommandSnapshot;
        ValidateAppendRequest(current, request);

        var replayed = await ReplayCaseAsync(lease, cancellationToken).ConfigureAwait(false);
        var priorOwnerId = replayed.AccountableOwnerId ?? throw InvalidJournal();
        foreach (var @event in request.Events)
        {
            replayed.Apply(@event);
        }

        var eventIds = request.Events.Select(@event => @event.EventId).ToImmutableArray();
        var targetActorId = request.AssignmentTargetActorId;
        var now = request.Events[^1].RecordedAtUtc;
        var newOwnerId = targetActorId ?? priorOwnerId;
        var ownerChanged = !string.Equals(newOwnerId, priorOwnerId, StringComparison.Ordinal);
        var lastEvent = request.Events[^1];
        var requiredOperations = request.Authorization.RequiredOperations.Select(operation => operation.ToString()).ToArray();

        var preparedEvents = new List<PreparedCaseEvent>(request.Events.Length);
        foreach (var @event in request.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (@event is not (CaseAssigned or CaseReportCorrected or CaseActionRequested or CaseActionRequestReviewed or CaseResolved or CaseReopened
                or CaseObservationRecorded or CaseFollowUpScheduled or CaseFollowUpAcknowledged or CaseFollowUpOutcomeRecorded
                or CaseEscalationRaised or CaseEscalationAcknowledged or CaseEscalationActionRecorded or CaseEscalationClosed or CaseAssignmentAcknowledged
                or CaseCollaboratorGranted or CaseCollaboratorRevoked
                or AssistanceRequested or AssistanceActivityRecorded or ActionOwnerChanged))
            {
                // Non-creation command processing must not create a first-report event.
                throw InvalidJournal();
            }

            var eventType = @event.GetType().Name;
            var serialized = _eventSerializer.SerializeToUtf8Bytes(@event);
            try
            {
                var payloadId = Guid.NewGuid();
                var eventSchemaVersion = _eventSerializer.GetSchemaVersion(@event);
                var reference = await _payloadStore.StageProtectedAsync(
                    current.CaseId,
                    payloadId,
                    eventType,
                    eventSchemaVersion,
                    serialized,
                    cancellationToken).ConfigureAwait(false);
                if (reference.PayloadId != payloadId || string.IsNullOrWhiteSpace(reference.ProtectionPurposeVersion))
                {
                    throw new ProtectedCaseEventPayloadException();
                }

                preparedEvents.Add(new PreparedCaseEvent(@event, eventType, eventSchemaVersion, reference));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serialized);
            }
        }

        var updateResult = await GuardedProjectionUpdateAsync(
            current,
            request,
            newOwnerId,
            ownerChanged,
            replayed,
            requiredOperations,
            cancellationToken).ConfigureAwait(false);
        if (updateResult == ProjectionUpdateResult.NotFoundOrVersionChanged)
        {
            return new CaseCommandAppendOutcome(CaseCommandAppendStatus.CaseVersionConflict,
                ImmutableArray<Guid>.Empty, request.ExpectedAggregateVersion, request.ExpectedReportRevision);
        }

        if (updateResult == ProjectionUpdateResult.AuthorizationChanged)
        {
            return new CaseCommandAppendOutcome(CaseCommandAppendStatus.AuthorizationChanged,
                ImmutableArray<Guid>.Empty, request.ExpectedAggregateVersion, request.ExpectedReportRevision);
        }

        if (updateResult == ProjectionUpdateResult.AssignmentTargetDenied)
        {
            return new CaseCommandAppendOutcome(CaseCommandAppendStatus.AssignmentTargetDenied,
                ImmutableArray<Guid>.Empty, request.ExpectedAggregateVersion, request.ExpectedReportRevision);
        }

        if (!await MaintainCollaboratorProjectionAsync(current, request, replayed, cancellationToken).ConfigureAwait(false))
        {
            return new CaseCommandAppendOutcome(CaseCommandAppendStatus.AuthorizationChanged,
                ImmutableArray<Guid>.Empty, request.ExpectedAggregateVersion, request.ExpectedReportRevision);
        }

        if (ownerChanged)
        {
            await IncrementOwnerEpochsAsync(current, [priorOwnerId, newOwnerId], cancellationToken).ConfigureAwait(false);
        }

        var receipt = new CommandReceiptEntity
        {
            ReceiptId = Guid.NewGuid(),
            ActorId = snapshot.ActorId,
            CommandId = snapshot.CommandId,
            CaseId = snapshot.CaseId,
            CommandType = snapshot.CommandType,
            SchemaVersion = snapshot.ReceiptProtocolVersion,
            CanonicalPayloadHash = snapshot.CanonicalPayloadHash,
            FirstAggregateVersion = request.Events[0].AggregateVersion,
            LastAggregateVersion = lastEvent.AggregateVersion,
            ReportRevision = lastEvent.ReportRevision,
            AcceptedAtUtc = now
        };
        _dbContext.CommandReceipts.Add(receipt);

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

        foreach (var assignment in request.Events.OfType<CaseAssigned>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recipientActorId = assignment.AccountableOwnerId;
            var occurrenceKey = $"{assignment.EventId:N}:{recipientActorId}";
            var notification = NotificationItem.Create(
                assignment.EventId,
                occurrenceKey,
                recipientActorId,
                NotificationKind.CaseNeedsAttention,
                NotificationResourceKind.ManagedCase,
                assignment.CaseId,
                NotificationAction.OpenCase,
                NotificationRecipientRequirement.CurrentAccountableOwner,
                assignment.RecordedAtUtc);

            _dbContext.NotificationItems.Add(notification);
            _dbContext.NotificationDeliveryOutbox.Add(new NotificationDeliveryOutboxEntity
            {
                OutboxId = Guid.NewGuid(),
                NotificationId = notification.NotificationId,
                CreatedAtUtc = assignment.RecordedAtUtc,
                AvailableAtUtc = assignment.RecordedAtUtc
            });
        }

        await MaintainMonitoringProjectionsAsync(request.Events, cancellationToken).ConfigureAwait(false);

        _dbContext.SecurityAuditEntries.Add(new SecurityAuditEntryEntity
        {
            AuditId = Guid.NewGuid(),
            ActorId = snapshot.ActorId,
            TargetActorId = ownerChanged ? newOwnerId : null,
            ResourceId = current.CaseId,
            Operation = $"CaseCommand.{snapshot.CommandType}",
            DecisionCode = AccessDecisionCode.Allowed.ToString(),
            AccessEpoch = request.Authorization.AccessEpoch,
            RecordedAtUtc = now
        });

        return CaseCommandAppendOutcome.Staged(eventIds, replayed.AggregateVersion, replayed.ReportRevision);
    }

    /// <summary>
    /// Maintains the case-monitoring read models from the decided events inside the same
    /// lease transaction as the guarded projection update. Observation, schedule, and
    /// raise events insert rows; outcome, acknowledge, and close events update the status
    /// of the row created by the matching schedule or raise. Only event fields are copied.
    /// </summary>
    private async ValueTask MaintainMonitoringProjectionsAsync(
        IReadOnlyList<CaseEvent> events,
        CancellationToken cancellationToken)
    {
        foreach (var @event in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (@event)
            {
                case CaseObservationRecorded observation:
                    _dbContext.ManagedCaseObservations.Add(new ManagedCaseObservationEntity
                    {
                        ObservationId = observation.ObservationId,
                        CaseId = observation.CaseId,
                        Narrative = observation.Narrative,
                        Source = observation.Source,
                        AggregateVersion = observation.AggregateVersion
                    });
                    break;
                case CaseFollowUpScheduled scheduled:
                    _dbContext.ManagedCaseFollowUps.Add(new ManagedCaseFollowUpEntity
                    {
                        FollowUpId = scheduled.FollowUpId,
                        CaseId = scheduled.CaseId,
                        Purpose = scheduled.Purpose,
                        AssigneeActorId = scheduled.AssigneeActorId,
                        DueAtUtc = scheduled.DueAtUtc,
                        Status = FollowUpStatus.Scheduled,
                        ScheduledVersion = scheduled.AggregateVersion,
                        CurrentScheduleVersion = scheduled.AggregateVersion,
                        CurrentScheduleEventId = scheduled.EventId,
                        // One records a known unacknowledged task; zero marks an owner
                        // transfer and null is reserved for pre-projection history.
                        AcknowledgedVersion = 1,
                        OutcomeVersion = null
                    });
                    break;
                case CaseFollowUpAcknowledged acknowledgement:
                    var acknowledgedFollowUp = await _dbContext.ManagedCaseFollowUps
                        .SingleOrDefaultAsync(item => item.FollowUpId == acknowledgement.FollowUpId, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw InvalidJournal();
                    if (acknowledgedFollowUp.CaseId != acknowledgement.CaseId ||
                        acknowledgedFollowUp.Status != FollowUpStatus.Scheduled ||
                        !string.Equals(acknowledgedFollowUp.AssigneeActorId, acknowledgement.ActorId, StringComparison.Ordinal))
                        throw InvalidJournal();
                    acknowledgedFollowUp.Status = FollowUpStatus.Acknowledged;
                    acknowledgedFollowUp.AcknowledgedVersion = acknowledgement.AggregateVersion;
                    break;
                case ActionOwnerChanged ownerChange when ownerChange.ActionKind == ActionOwnerKind.FollowUp:
                    var transferredFollowUp = await _dbContext.ManagedCaseFollowUps
                        .SingleOrDefaultAsync(item => item.FollowUpId == ownerChange.ActionId, cancellationToken)
                        .ConfigureAwait(false) ?? throw InvalidJournal();
                    if (transferredFollowUp.CaseId != ownerChange.CaseId ||
                        transferredFollowUp.Status is FollowUpStatus.Completed or FollowUpStatus.Cancelled ||
                        !string.Equals(transferredFollowUp.AssigneeActorId, ownerChange.PreviousOwnerId, StringComparison.Ordinal))
                        throw InvalidJournal();
                    transferredFollowUp.AssigneeActorId = ownerChange.NewOwnerId;
                    // Zero marks a transfer so an inherited Acknowledged status cannot
                    // be attributed to the new owner. A later acknowledgment replaces it.
                    transferredFollowUp.AcknowledgedVersion = 0;
                    break;
                case CaseFollowUpOutcomeRecorded outcome:
                    var followUp = await _dbContext.ManagedCaseFollowUps
                        .SingleOrDefaultAsync(item => item.FollowUpId == outcome.FollowUpId, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw InvalidJournal();
                    followUp.DueAtUtc = outcome.NewDueAtUtc ?? followUp.DueAtUtc;
                    followUp.Status = outcome.Outcome switch
                    {
                        FollowUpOutcome.Completed => FollowUpStatus.Completed,
                        FollowUpOutcome.Missed => FollowUpStatus.Missed,
                        FollowUpOutcome.Unreachable => FollowUpStatus.Unreachable,
                        FollowUpOutcome.Cancelled => FollowUpStatus.Cancelled,
                        FollowUpOutcome.Rescheduled => FollowUpStatus.Scheduled,
                        _ => throw InvalidJournal()
                    };
                    followUp.OutcomeVersion = outcome.AggregateVersion;
                    if (outcome.Outcome == FollowUpOutcome.Rescheduled)
                    {
                        followUp.CurrentScheduleVersion = outcome.AggregateVersion;
                        followUp.CurrentScheduleEventId = outcome.EventId;
                    }
                    break;
                case CaseEscalationRaised raised:
                    _dbContext.ManagedCaseEscalations.Add(new ManagedCaseEscalationEntity
                    {
                        EscalationId = raised.EscalationId,
                        CaseId = raised.CaseId,
                        Status = EscalationStatus.Raised,
                        RaisedVersion = raised.AggregateVersion,
                        AcknowledgedVersion = null,
                        ClosedVersion = null
                    });
                    break;
                case CaseEscalationAcknowledged acknowledged:
                    var toAcknowledge = await _dbContext.ManagedCaseEscalations
                        .SingleOrDefaultAsync(item => item.EscalationId == acknowledged.EscalationId, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw InvalidJournal();
                    toAcknowledge.Status = EscalationStatus.Acknowledged;
                    toAcknowledge.AcknowledgedVersion = acknowledged.AggregateVersion;
                    if (!string.IsNullOrWhiteSpace(acknowledged.ActionSummary))
                    {
                        toAcknowledge.Status = EscalationStatus.ActionRecorded;
                        toAcknowledge.ActionRecordedVersion = acknowledged.AggregateVersion;
                        toAcknowledge.ActionSummary = acknowledged.ActionSummary;
                    }
                    break;
                case CaseEscalationActionRecorded action:
                    var toRecordAction = await _dbContext.ManagedCaseEscalations
                        .SingleOrDefaultAsync(item => item.EscalationId == action.EscalationId, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw InvalidJournal();
                    toRecordAction.Status = EscalationStatus.ActionRecorded;
                    toRecordAction.ActionRecordedVersion = action.AggregateVersion;
                    toRecordAction.ActionSummary = action.ActionSummary;
                    break;
                case CaseEscalationClosed closed:
                    var toClose = await _dbContext.ManagedCaseEscalations
                        .SingleOrDefaultAsync(item => item.EscalationId == closed.EscalationId, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw InvalidJournal();
                    toClose.Status = EscalationStatus.Closed;
                    toClose.ClosedVersion = closed.AggregateVersion;
                    break;
                case AssistanceRequested requested:
                    _dbContext.ManagedCaseAssistance.Add(new ManagedCaseAssistanceEntity
                    {
                        AssistanceId = requested.AssistanceId,
                        CaseId = requested.CaseId,
                        Need = requested.Need,
                        ResponsibleActorId = requested.ResponsibleActorId,
                        RequestedByActorId = requested.ActorId,
                        RequestedAtUtc = requested.RecordedAtUtc,
                        DueAtUtc = requested.DueAtUtc,
                        Status = AssistanceStatus.Requested,
                        LastNote = requested.Need,
                        LastEventVersion = requested.AggregateVersion
                    });
                    break;
                case AssistanceActivityRecorded activity:
                    var assistance = await _dbContext.ManagedCaseAssistance
                        .SingleOrDefaultAsync(item => item.AssistanceId == activity.AssistanceId, cancellationToken)
                        .ConfigureAwait(false) ?? throw InvalidJournal();
                    assistance.Status = activity.Status;
                    assistance.LastNote = activity.Note;
                    assistance.LastEventVersion = activity.AggregateVersion;
                    if (activity.Status == AssistanceStatus.Accepted)
                        assistance.AcceptedByActorId = activity.ActorId;
                    if (activity.Status == AssistanceStatus.Provided)
                        assistance.ProvidedByActorId = activity.ActorId;
                    if (activity.Status == AssistanceStatus.ReceiptConfirmed)
                    {
                        assistance.ReceiptRecordedByActorId = activity.ActorId;
                        assistance.ReceiptMethod = activity.ReceiptMethod;
                        assistance.ReceiptContext = activity.ReceiptContext;
                    }
                    break;
                case ActionOwnerChanged assistanceOwnerChange when assistanceOwnerChange.ActionKind == ActionOwnerKind.Assistance:
                    var transferredAssistance = await _dbContext.ManagedCaseAssistance
                        .SingleOrDefaultAsync(item => item.AssistanceId == assistanceOwnerChange.ActionId, cancellationToken)
                        .ConfigureAwait(false) ?? throw InvalidJournal();
                    if (transferredAssistance.CaseId != assistanceOwnerChange.CaseId ||
                        transferredAssistance.Status is AssistanceStatus.ReceiptConfirmed or AssistanceStatus.Unfulfilled or AssistanceStatus.Cancelled ||
                        !string.Equals(transferredAssistance.ResponsibleActorId, assistanceOwnerChange.PreviousOwnerId, StringComparison.Ordinal))
                        throw InvalidJournal();
                    transferredAssistance.ResponsibleActorId = assistanceOwnerChange.NewOwnerId;
                    transferredAssistance.LastEventVersion = assistanceOwnerChange.AggregateVersion;
                    break;
                default:
                    break;
            }
        }
    }

    private sealed record PreparedCaseEvent(
        CaseEvent Event,
        string EventType,
        int SchemaVersion,
        ProtectedCaseEventPayloadReference PayloadReference);

    public async ValueTask StageDeniedAuditAsync(
        ICaseCommandLease lease,
        CaseCommandDenialAudit audit,
        CancellationToken cancellationToken)
    {
        var current = RequireLease(lease, CaseLeaseMode.Write);
        ArgumentNullException.ThrowIfNull(audit);
        if (audit.CaseId != current.CaseId || audit.CommandId == Guid.Empty ||
            !string.Equals(audit.ActorId, current.PrimaryActorId, StringComparison.Ordinal) ||
            !Enum.IsDefined(audit.DecisionCode) || audit.DecisionCode == AccessDecisionCode.Allowed)
        {
            throw new ArgumentException("The denial audit does not match the active case lease.", nameof(audit));
        }

        RequireCanonicalActor(audit.ActorId);
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
            ResourceId = audit.CaseId,
            Operation = "CaseCommand.Denied",
            DecisionCode = audit.DecisionCode.ToString(),
            AccessEpoch = accessEpoch.Value,
            RecordedAtUtc = now
        });
    }

    private async Task<ProjectionUpdateResult> GuardedProjectionUpdateAsync(
        PostgresCaseCommandLease lease,
        CaseCommandAppendRequest request,
        string newOwnerId,
        bool ownerChanged,
        CaseAggregate next,
        string[] requiredOperations,
        CancellationToken cancellationToken)
    {
        var sql = $$"""
            UPDATE public.managed_cases AS mc
            SET aggregate_version = @new_aggregate_version,
                report_revision = @new_report_revision,
                resource_version = mc.resource_version + 1,
                workflow = @new_workflow,
                workflow_version = @new_workflow_version,
                accountable_owner_actor_id = @new_owner_actor_id,
                updated_at_utc = clock_timestamp()
            FROM public.actor_access_states AS access_state,
                 public.actors AS actor,
                 public.organizations AS org,
                 public.workspaces AS workspace,
                 public.organization_memberships AS member,
                 public.access_grant_groups AS authority
            WHERE mc.case_id = @case_id
              AND mc.aggregate_version = @expected_aggregate_version
              AND mc.report_revision = @expected_report_revision
              AND mc.resource_version = @expected_resource_version
              AND access_state.actor_id = @actor_id
              AND access_state.access_epoch = @expected_access_epoch
              AND actor.actor_id = access_state.actor_id
              AND actor.status = 'Active'
              AND (actor.expires_at_utc IS NULL OR actor.expires_at_utc > clock_timestamp())
              AND org.organization_id = mc.organization_id AND org.status = 'Active'
              AND workspace.workspace_id = mc.workspace_id
              AND workspace.status = 'Active'
              AND EXISTS (SELECT 1 FROM public.workspace_organizations workspace_link
                  WHERE workspace_link.workspace_id = mc.workspace_id AND workspace_link.organization_id = mc.organization_id
                    AND workspace_link.status = 'Active')
              AND member.actor_id = actor.actor_id
              AND member.organization_id = mc.organization_id
              AND member.status = 'Active'
              AND member.valid_from_utc <= clock_timestamp()
              AND (member.expires_at_utc IS NULL OR member.expires_at_utc > clock_timestamp())
              AND authority.grant_group_id = @grant_group_id
              AND authority.actor_id = actor.actor_id
              AND authority.membership_id = member.membership_id
              AND authority.organization_id = mc.organization_id
              AND authority.workspace_id = mc.workspace_id
              AND authority.country_code = mc.country_code
              AND authority.sector = mc.sector::text
              AND authority.sensitivity_code = mc.sensitivity_code
              AND authority.status = 'Active'
              AND authority.valid_from_utc <= clock_timestamp()
              AND authority.expires_at_utc > clock_timestamp()
              AND {{AllowedOperationsSql}}
              AND {{CaseRelationshipSql}}
              AND (@owner_changed = FALSE OR EXISTS (
                  SELECT 1
                  FROM public.actors AS target_actor
                  JOIN public.actor_access_states AS target_state ON target_state.actor_id = target_actor.actor_id
                  JOIN public.organization_memberships AS target_member
                    ON target_member.actor_id = target_actor.actor_id
                   AND target_member.organization_id = mc.organization_id
                  JOIN public.access_grant_groups AS target_group
                    ON target_group.membership_id = target_member.membership_id
                   AND target_group.actor_id = target_actor.actor_id
                   AND target_group.organization_id = mc.organization_id
                   AND target_group.workspace_id = mc.workspace_id
                   AND target_group.country_code = mc.country_code
                   AND target_group.sector = mc.sector::text
                   AND target_group.sensitivity_code = mc.sensitivity_code
                  JOIN public.access_grant_group_operations AS target_read
                    ON target_read.grant_group_id = target_group.grant_group_id
                   AND target_read.operation = 'ReadCase'
                  WHERE target_actor.actor_id = @new_owner_actor_id
                    AND target_actor.status = 'Active'
                    AND (target_actor.expires_at_utc IS NULL OR target_actor.expires_at_utc > clock_timestamp())
                    AND target_member.status = 'Active'
                    AND target_member.valid_from_utc <= clock_timestamp()
                    AND (target_member.expires_at_utc IS NULL OR target_member.expires_at_utc > clock_timestamp())
                    AND target_group.status = 'Active'
                    AND target_group.valid_from_utc <= clock_timestamp()
                    AND target_group.expires_at_utc > clock_timestamp()))
            RETURNING mc.case_id
            """;

        await using var command = CreateCommand(lease, sql,
            ("new_aggregate_version", next.AggregateVersion),
            ("new_report_revision", next.ReportRevision),
            ("new_workflow", next.Workflow.ToString()),
            ("new_workflow_version", next.WorkflowVersion),
            ("new_owner_actor_id", newOwnerId),
            ("case_id", lease.CaseId),
            ("expected_aggregate_version", request.ExpectedAggregateVersion),
            ("expected_report_revision", request.ExpectedReportRevision),
            ("expected_resource_version", request.ExpectedResourceVersion),
            ("actor_id", request.Authorization.ActorId),
            ("expected_access_epoch", request.Authorization.AccessEpoch),
            ("grant_group_id", request.Authorization.MatchedGrantGroupId),
            ("required_operations", requiredOperations),
            ("owner_changed", ownerChanged));

        var updatedCase = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (updatedCase is not null and not DBNull)
        {
            return ProjectionUpdateResult.Updated;
        }

        var current = await _dbContext.ManagedCases.AsNoTracking()
            .Where(entity => entity.CaseId == lease.CaseId)
            .Select(entity => new { entity.AggregateVersion, entity.ReportRevision, entity.ResourceVersion })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null || current.AggregateVersion != request.ExpectedAggregateVersion ||
            current.ReportRevision != request.ExpectedReportRevision || current.ResourceVersion != request.ExpectedResourceVersion)
        {
            return ProjectionUpdateResult.NotFoundOrVersionChanged;
        }

        if (ownerChanged && !await IsEligibleAssignmentTargetAsync(
                lease,
                newOwnerId,
                new CaseCommandCaseHeader(lease.CaseId, current.AggregateVersion, current.ReportRevision, current.ResourceVersion, newOwnerId),
                cancellationToken).ConfigureAwait(false))
        {
            return ProjectionUpdateResult.AssignmentTargetDenied;
        }

        return ProjectionUpdateResult.AuthorizationChanged;
    }

    private async Task IncrementOwnerEpochsAsync(
        PostgresCaseCommandLease lease,
        IEnumerable<string> actorIds,
        CancellationToken cancellationToken)
    {
        var orderedIds = actorIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (orderedIds.Any(id => !lease.LockedActorIds.Contains(id, StringComparer.Ordinal)))
        {
            throw new CasePersistenceRaceException(CasePersistenceRaceKind.AuthorityDiscoveryChanged);
        }

        const string sql = """
            UPDATE public.actor_access_states
            SET access_epoch = access_epoch + 1,
                version = version + 1,
                updated_at_utc = clock_timestamp()
            WHERE actor_id = ANY (@actor_ids)
            RETURNING actor_id
            """;
        await using var command = CreateCommand(lease, sql, ("actor_ids", orderedIds));
        var updated = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            updated.Add(reader.GetString(0));
        }

        if (updated.Count != orderedIds.Length || orderedIds.Any(id => !updated.Contains(id)))
        {
            throw new CasePersistenceRaceException(CasePersistenceRaceKind.AuthorityDiscoveryChanged);
        }
    }

    private PostgresCaseCommandLease RequireLease(ICaseCommandLease lease, params CaseLeaseMode[] modes)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var current = PostgresLeaseGuard.RequireCurrent(_dbContext, _leaseAccessor, lease.CaseId);
        if (!ReferenceEquals(current, lease) || !modes.Contains(current.Mode))
        {
            throw new InvalidOperationException("The active case lease is unavailable for this operation.");
        }

        return current;
    }

    private DbCommand CreateCommand(
        PostgresCaseCommandLease lease,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = _dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("A PostgreSQL transaction is required for case command persistence.");
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static void ValidateAppendRequest(PostgresCaseCommandLease lease, CaseCommandAppendRequest request)
    {
        if (request.Events.IsDefaultOrEmpty || request.ExpectedAggregateVersion > long.MaxValue - request.Events.Length)
        {
            throw new InvalidOperationException("The accepted command does not match the active lease contract.");
        }

        var snapshot = request.CommandSnapshot;
        var authorization = request.Authorization;
        var targetEvents = request.Events
            .Where(@event => @event is CaseAssigned or CaseReopened)
            .ToArray();
        if (targetEvents.Length > 1)
        {
            throw new InvalidOperationException("An accepted command cannot contain ambiguous assignment targets.");
        }

        var eventTarget = targetEvents.SingleOrDefault() switch
        {
            CaseAssigned assignment => assignment.AccountableOwnerId,
            CaseReopened reopening => reopening.AccountableOwnerId,
            null => null,
            _ => throw new InvalidOperationException("The accepted command contains an unsupported assignment event.")
        };
        if (snapshot.CaseId != lease.CaseId || snapshot.CommandId != request.Events[0].CommandId ||
            !string.Equals(snapshot.ActorId, authorization.ActorId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ActorId, lease.PrimaryActorId, StringComparison.Ordinal) ||
            snapshot.ReceiptProtocolVersion != CanonicalCaseCommandHasher.CurrentReceiptProtocolVersion ||
            snapshot.WireCommandSchemaVersion != CanonicalCaseCommandHasher.CurrentWireCommandSchemaVersion ||
            authorization.AccessEpoch < 0 || string.IsNullOrWhiteSpace(authorization.MatchedGrantGroupId) ||
            authorization.RequiredOperations.IsDefaultOrEmpty || authorization.RequiredOperations.Any(operation => !Enum.IsDefined(operation)) ||
            request.ExpectedAggregateVersion < 2 || request.ExpectedReportRevision < 1 || request.ExpectedResourceVersion < 1 ||
            snapshot.Command.ExpectedVersion != request.ExpectedAggregateVersion ||
            !string.Equals(eventTarget, request.AssignmentTargetActorId, StringComparison.Ordinal) ||
            request.Events.Any(@event =>
                @event.CaseId != lease.CaseId || @event.CommandId != snapshot.CommandId ||
                !string.Equals(@event.ActorId, snapshot.ActorId, StringComparison.Ordinal) ||
                @event.RecordedAtUtc.Offset != TimeSpan.Zero || @event.AggregateVersion <= request.ExpectedAggregateVersion) ||
            request.Events.Select((@event, index) => @event.AggregateVersion == request.ExpectedAggregateVersion + index + 1)
                .Any(isConsecutive => !isConsecutive))
        {
            throw new InvalidOperationException("The accepted command does not match the active lease contract.");
        }
    }

    private static void RequireCanonicalActor(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId) || !string.Equals(actorId, actorId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical actor identifier is required.", nameof(actorId));
        }
    }

    private static InvalidDataException InvalidJournal() =>
        new("The persisted case event journal is invalid or unsupported.");

    private static InvalidDataException InvalidReceipt() =>
        new("The persisted command receipt is invalid or unsupported.");

    private enum ProjectionUpdateResult
    {
        Updated,
        NotFoundOrVersionChanged,
        AuthorizationChanged,
        AssignmentTargetDenied
    }
}
