using System.Collections.Immutable;
using System.Security.Cryptography;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases.Commands;

/// <summary>Coordinates authorization, idempotency, domain decisions, and lease-scoped staging.</summary>
public sealed class CaseCommandProcessor : ICaseCommandProcessor
{
    private readonly ICaseCommandUnitOfWork _unitOfWork;
    private readonly ICaseAccessPolicy _accessPolicy;
    private readonly ICaseCommandPersistence _persistence;
    private readonly CaseCommandProcessingOptions _options;

    public CaseCommandProcessor(
        ICaseCommandUnitOfWork unitOfWork,
        ICaseAccessPolicy accessPolicy,
        ICaseCommandPersistence persistence,
        CaseCommandProcessingOptions? options = null)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _accessPolicy = accessPolicy ?? throw new ArgumentNullException(nameof(accessPolicy));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _options = options ?? new CaseCommandProcessingOptions();
        _options.Validate();
    }

    public async ValueTask<CaseCommandProcessingResult> ProcessAsync(
        ActorContext actor,
        Guid caseId,
        int wireCommandSchemaVersion,
        CaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);

        CanonicalCaseCommandSnapshot commandSnapshot;
        try
        {
            commandSnapshot = CanonicalCaseCommandHasher.CreateSnapshot(
                actor,
                caseId,
                CanonicalCaseCommandHasher.CurrentReceiptProtocolVersion,
                wireCommandSchemaVersion,
                command);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Result(CaseCommandProcessingStatus.Invalid, caseId, command.CommandId);
        }

        // A target from the request is only a lock hint after the command has been
        // authorized and its current event history has been replayed. Start without
        // body-derived target hints so receipt replays never inspect target authority.
        var actorHints = ImmutableArray<string>.Empty;
        for (var attempt = 1; attempt <= _options.MaximumTransactionAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await ProcessAttemptAsync(actor, commandSnapshot, actorHints, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CaseLeaseDiscoveryChangedException exception)
            {
                actorHints = exception.AdditionalActorHints;
                if (attempt == _options.MaximumTransactionAttempts)
                {
                    return Result(CaseCommandProcessingStatus.Unavailable, caseId, commandSnapshot.CommandId);
                }
            }
            catch (CasePersistenceRaceException) when (attempt < _options.MaximumTransactionAttempts)
            {
                // The next loop starts a new lease and repeats authorization and receipt lookup.
            }
            catch (CasePersistenceRaceException)
            {
                return Result(CaseCommandProcessingStatus.Unavailable, caseId, commandSnapshot.CommandId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return Result(CaseCommandProcessingStatus.Unavailable, caseId, commandSnapshot.CommandId);
            }
        }

        return Result(CaseCommandProcessingStatus.Unavailable, caseId, commandSnapshot.CommandId);
    }

    private async ValueTask<CaseCommandProcessingResult> ProcessAttemptAsync(
        ActorContext actor,
        CanonicalCaseCommandSnapshot commandSnapshot,
        ImmutableArray<string> actorHints,
        CancellationToken cancellationToken)
    {
        var caseId = commandSnapshot.CaseId;
        var commandId = commandSnapshot.CommandId;
        await using var lease = await _unitOfWork.BeginCaseAsync(
            actor,
            caseId,
            actorHints,
            CaseLeaseMode.Write,
            cancellationToken).ConfigureAwait(false);

        EnsureLease(lease, caseId, CaseLeaseMode.Write);

        var preliminaryOperation = GetPreliminaryOperation(commandSnapshot.Command);
        var preliminaryOperations = IncludeReadCase([preliminaryOperation]);
        var preliminaryDecision = await _accessPolicy.AuthorizeAllAsync(
            actor,
            caseId,
            preliminaryOperations,
            cancellationToken).ConfigureAwait(false);
        if (!preliminaryDecision.IsAllowed)
        {
            return await DenyAndCommitAuditAsync(
                lease,
                actor,
                caseId,
                commandId,
                preliminaryDecision.Code,
                cancellationToken).ConfigureAwait(false);
        }

        var header = await _persistence.ReadCaseHeaderAsync(lease, cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            return await DenyAndCommitAuditAsync(
                lease,
                actor,
                caseId,
                commandId,
                AccessDecisionCode.ResourceMissing,
                cancellationToken).ConfigureAwait(false);
        }

        if (header.CaseId != caseId || header.AggregateVersion < 2 || header.ReportRevision < 1 ||
            header.ResourceVersion < 1 || string.IsNullOrWhiteSpace(header.AccountableOwnerActorId))
        {
            return Result(CaseCommandProcessingStatus.Unavailable, caseId, commandId);
        }

        var aggregate = await _persistence.ReplayCaseAsync(lease, cancellationToken).ConfigureAwait(false);
        if (aggregate.CaseId != caseId || aggregate.AggregateVersion != header.AggregateVersion ||
            aggregate.ReportRevision != header.ReportRevision)
        {
            return Result(CaseCommandProcessingStatus.Unavailable, caseId, commandId);
        }

        CaseCommandAuthorizationRequirement requirement;
        try
        {
            requirement = CaseCommandAuthorizationMap.Resolve(commandSnapshot.Command, aggregate);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return Result(CaseCommandProcessingStatus.Invalid, caseId, commandId);
        }

        var authorizedOperations = IncludeReadCase(requirement.RequiredOperations);
        var fullDecision = await _accessPolicy.AuthorizeAllAsync(
            actor,
            caseId,
            authorizedOperations,
            cancellationToken).ConfigureAwait(false);
        if (!fullDecision.IsAllowed)
        {
            return await DenyAndCommitAuditAsync(
                lease,
                actor,
                caseId,
                commandId,
                fullDecision.Code,
                cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(fullDecision.MatchedGrantGroupId))
        {
            return Result(CaseCommandProcessingStatus.Unavailable, caseId, commandId);
        }

        var receipt = await _persistence.FindReceiptAsync(
            lease,
            actor.ActorId,
            commandId,
            cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            return ReplayOrConflict(commandSnapshot, receipt);
        }

        if (commandSnapshot.Command.ExpectedVersion != aggregate.AggregateVersion)
        {
            return Result(CaseCommandProcessingStatus.Conflict, caseId, commandId);
        }

        var databaseNow = (await _persistence.GetDatabaseUtcNowAsync(lease, cancellationToken)
            .ConfigureAwait(false)).ToUniversalTime();
        var commandForDecision = WithServerRecordedAt(commandSnapshot.Command, databaseNow);
        var capabilities = new ActorCapabilities(actor.ActorId, requirement.RequiredCapabilities);

        IReadOnlyList<CaseEvent> decidedEvents;
        try
        {
            decidedEvents = aggregate.Decide(commandForDecision, capabilities);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            return Result(CaseCommandProcessingStatus.Conflict, caseId, commandId);
        }

        var eventSnapshot = decidedEvents.ToImmutableArray();
        if (eventSnapshot.IsDefaultOrEmpty || eventSnapshot.Any(@event =>
                @event.CaseId != caseId || @event.CommandId != commandId ||
                !string.Equals(@event.ActorId, actor.ActorId, StringComparison.Ordinal) ||
                @event.RecordedAtUtc.Offset != TimeSpan.Zero))
        {
            return Result(CaseCommandProcessingStatus.Unavailable, caseId, commandId);
        }

        var targetActorId = GetAssignmentTarget(eventSnapshot);
        if (targetActorId is not null)
        {
            if (!lease.LockedActorIds.Contains(targetActorId, StringComparer.Ordinal))
            {
                throw new CaseLeaseDiscoveryChangedException(caseId, actorHints.Add(targetActorId));
            }

            var targetEligible = await _persistence.IsEligibleAssignmentTargetAsync(
                lease,
                targetActorId,
                header,
                cancellationToken).ConfigureAwait(false);
            if (!targetEligible)
            {
                return await DenyAndCommitAuditAsync(
                    lease,
                    actor,
                    caseId,
                    commandId,
                    AccessDecisionCode.OperationNotGranted,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (commandForDecision is RequestAssistanceCommand assistanceRequest)
        {
            var responsibleActorId = assistanceRequest.ResponsibleActorId;
            if (!lease.LockedActorIds.Contains(responsibleActorId, StringComparer.Ordinal))
            {
                throw new CaseLeaseDiscoveryChangedException(caseId, actorHints.Add(responsibleActorId));
            }

            if (!await _persistence.IsEligibleAssignmentTargetAsync(
                    lease, responsibleActorId, header, cancellationToken).ConfigureAwait(false))
            {
                return await DenyAndCommitAuditAsync(lease, actor, caseId, commandId,
                    AccessDecisionCode.OperationNotGranted, cancellationToken).ConfigureAwait(false);
            }
        }

        var collaboratorActorId = commandForDecision switch
        {
            GrantCaseCollaboratorCommand grant => grant.CollaboratorActorId,
            RevokeCaseCollaboratorCommand revoke => aggregate.Collaborators.Single(item => item.GrantId == revoke.GrantId).ActorId,
            _ => null
        };
        if (collaboratorActorId is not null && !lease.LockedActorIds.Contains(collaboratorActorId, StringComparer.Ordinal))
        {
            throw new CaseLeaseDiscoveryChangedException(caseId, actorHints.Add(collaboratorActorId));
        }

        if (commandForDecision is GrantCaseCollaboratorCommand collaborator)
        {
            if (!await _persistence.IsEligibleCollaboratorAsync(lease, collaborator.CollaboratorActorId,
                    collaborator.Actions, header, cancellationToken).ConfigureAwait(false))
            {
                return await DenyAndCommitAuditAsync(lease, actor, caseId, commandId,
                    AccessDecisionCode.OperationNotGranted, cancellationToken).ConfigureAwait(false);
            }
        }

        var authorization = new CaseCommandAuthorizationSnapshot(
            actor.ActorId,
            actor.AccessEpoch,
            fullDecision.MatchedGrantGroupId,
            authorizedOperations.ToImmutableArray(),
            header.ResourceVersion);
        var appendRequest = new CaseCommandAppendRequest(
            commandSnapshot,
            authorization,
            eventSnapshot,
            header.AggregateVersion,
            header.ReportRevision,
            header.ResourceVersion,
            targetActorId);
        var appendOutcome = await _persistence.StageAcceptedCommandAsync(
            lease,
            appendRequest,
            cancellationToken).ConfigureAwait(false);

        switch (appendOutcome.Status)
        {
            case CaseCommandAppendStatus.CaseVersionConflict:
                return Result(CaseCommandProcessingStatus.Conflict, caseId, commandId);
            case CaseCommandAppendStatus.AuthorizationChanged:
                // A failed append may already have staged ciphertext or other tracked rows.
                // Disposing this lease rolls all of it back; do not commit an audit on it.
                return Result(CaseCommandProcessingStatus.Denied, caseId, commandId);
            case CaseCommandAppendStatus.AssignmentTargetDenied:
                return Result(CaseCommandProcessingStatus.Denied, caseId, commandId);
            case CaseCommandAppendStatus.Staged:
                if (!appendOutcome.EventIds.SequenceEqual(eventSnapshot.Select(@event => @event.EventId)) ||
                    appendOutcome.AggregateVersion != eventSnapshot[^1].AggregateVersion ||
                    appendOutcome.ReportRevision != eventSnapshot[^1].ReportRevision)
                {
                    return Result(CaseCommandProcessingStatus.Unavailable, caseId, commandId);
                }

                await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new CaseCommandProcessingResult(
                    CaseCommandProcessingStatus.Accepted,
                    caseId,
                    commandId,
                    appendOutcome.EventIds,
                    appendOutcome.AggregateVersion,
                    appendOutcome.ReportRevision);
            default:
                return Result(CaseCommandProcessingStatus.Unavailable, caseId, commandId);
        }
    }

    private async ValueTask<CaseCommandProcessingResult> DenyAndCommitAuditAsync(
        ICaseCommandLease lease,
        ActorContext actor,
        Guid caseId,
        Guid commandId,
        AccessDecisionCode decisionCode,
        CancellationToken cancellationToken)
    {
        await _persistence.StageDeniedAuditAsync(
            lease,
            new CaseCommandDenialAudit(actor.ActorId, caseId, commandId, decisionCode),
            cancellationToken).ConfigureAwait(false);
        await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CaseCommandProcessingResult(
            CaseCommandProcessingStatus.Denied,
            caseId,
            commandId,
            ImmutableArray<Guid>.Empty,
            null,
            null,
            DenialAuditDurable: true);
    }

    private static CaseCommandProcessingResult ReplayOrConflict(
        CanonicalCaseCommandSnapshot commandSnapshot,
        CaseCommandReceipt receipt)
    {
        if (!string.Equals(receipt.ActorId, commandSnapshot.ActorId, StringComparison.Ordinal) ||
            receipt.CommandId != commandSnapshot.CommandId ||
            receipt.CaseId != commandSnapshot.CaseId ||
            !string.Equals(receipt.CommandType, commandSnapshot.CommandType, StringComparison.Ordinal))
        {
            return Result(CaseCommandProcessingStatus.Conflict, commandSnapshot.CaseId, commandSnapshot.CommandId);
        }

        if (receipt.SchemaVersion != CanonicalCaseCommandHasher.CurrentReceiptProtocolVersion)
        {
            return Result(CaseCommandProcessingStatus.Unavailable, commandSnapshot.CaseId, commandSnapshot.CommandId);
        }

        var expectedHash = commandSnapshot.CanonicalPayloadHash;
        var storedHash = receipt.CanonicalPayloadHash.IsDefault
            ? ReadOnlySpan<byte>.Empty
            : receipt.CanonicalPayloadHash.AsSpan();
        var matches = storedHash.Length == expectedHash.Length &&
            CryptographicOperations.FixedTimeEquals(storedHash, expectedHash);
        if (!matches)
        {
            return Result(CaseCommandProcessingStatus.Conflict, commandSnapshot.CaseId, commandSnapshot.CommandId);
        }

        if (receipt.AcceptedEventIds.IsDefaultOrEmpty || receipt.AcceptedEventIds.Any(id => id == Guid.Empty) ||
            receipt.AggregateVersion < 2 || receipt.ReportRevision < 1)
        {
            return Result(CaseCommandProcessingStatus.Unavailable, commandSnapshot.CaseId, commandSnapshot.CommandId);
        }

        return new CaseCommandProcessingResult(
            CaseCommandProcessingStatus.Replayed,
            commandSnapshot.CaseId,
            commandSnapshot.CommandId,
            receipt.AcceptedEventIds,
            receipt.AggregateVersion,
            receipt.ReportRevision);
    }

    private static CaseOperation GetPreliminaryOperation(CaseCommand command) => command switch
    {
        AcknowledgeAssignmentCommand => CaseOperation.AcknowledgeAssignment,
        GrantCaseCollaboratorCommand or RevokeCaseCollaboratorCommand => CaseOperation.ManageCollaborators,
        RequestAssistanceCommand or UpdateAssistanceCommand => CaseOperation.ManageAssistance,
        CorrectReportCommand => CaseOperation.CorrectReport,
        RequestCaseActionCommand => CaseOperation.RequestProtectedAction,
        ReviewCaseActionRequestCommand => CaseOperation.ReviewProtectedAction,
        ResolveCaseCommand => CaseOperation.ResolveCase,
        ReopenCaseCommand => CaseOperation.ReopenCase,
        RecordObservationCommand => CaseOperation.RecordObservation,
        ScheduleFollowUpCommand or AcknowledgeFollowUpCommand or RecordFollowUpOutcomeCommand => CaseOperation.ManageFollowUp,
        RaiseEscalationCommand or AcknowledgeEscalationCommand or CloseEscalationCommand => CaseOperation.ManageEscalation,
        _ => throw new NotSupportedException("The command is outside the non-creation command set.")
    };

    private static IReadOnlyList<CaseOperation> IncludeReadCase(IEnumerable<CaseOperation> operations) => operations
        .Append(CaseOperation.ReadCase)
        .Distinct()
        .OrderBy(operation => (int)operation)
        .ToArray();

    private static string? GetAssignmentTarget(ImmutableArray<CaseEvent> events) =>
        events.Select(@event => @event switch
            {
                CaseAssigned assignment => assignment.AccountableOwnerId,
                CaseReopened reopening => reopening.AccountableOwnerId,
                _ => null
            })
            .LastOrDefault(target => target is not null);

    private static CaseCommand WithServerRecordedAt(CaseCommand command, DateTimeOffset databaseNow) => command switch
    {
        AcknowledgeAssignmentCommand acknowledgement => acknowledgement with { RecordedAtUtc = databaseNow },
        GrantCaseCollaboratorCommand grant => grant with { RecordedAtUtc = databaseNow },
        RevokeCaseCollaboratorCommand revoke => revoke with { RecordedAtUtc = databaseNow },
        RequestAssistanceCommand request => request with { RecordedAtUtc = databaseNow },
        UpdateAssistanceCommand update => update with { RecordedAtUtc = databaseNow },
        CorrectReportCommand correction => correction with { RecordedAtUtc = databaseNow },
        RequestCaseActionCommand request => request with { RecordedAtUtc = databaseNow },
        ReviewCaseActionRequestCommand review => review with { RecordedAtUtc = databaseNow },
        ResolveCaseCommand resolution => resolution with { RecordedAtUtc = databaseNow },
        ReopenCaseCommand reopening => reopening with { RecordedAtUtc = databaseNow },
        RecordObservationCommand observation => observation with { RecordedAtUtc = databaseNow },
        ScheduleFollowUpCommand schedule => schedule with { RecordedAtUtc = databaseNow },
        AcknowledgeFollowUpCommand acknowledge => acknowledge with { RecordedAtUtc = databaseNow },
        RecordFollowUpOutcomeCommand outcome => outcome with { RecordedAtUtc = databaseNow },
        RaiseEscalationCommand escalation => escalation with { RecordedAtUtc = databaseNow },
        AcknowledgeEscalationCommand acknowledge => acknowledge with { RecordedAtUtc = databaseNow },
        CloseEscalationCommand close => close with { RecordedAtUtc = databaseNow },
        _ => throw new NotSupportedException("The command is outside the non-creation command set.")
    };

    private static void EnsureLease(ICaseCommandLease lease, Guid caseId, CaseLeaseMode mode)
    {
        if (!lease.IsActive || lease.CaseId != caseId || lease.Mode != mode)
        {
            throw new InvalidOperationException("The case command lease is unavailable.");
        }
    }

    private static CaseCommandProcessingResult Result(
        CaseCommandProcessingStatus status,
        Guid caseId,
        Guid commandId) => new(
        status,
        caseId,
        commandId,
        ImmutableArray<Guid>.Empty,
        null,
        null);
}
