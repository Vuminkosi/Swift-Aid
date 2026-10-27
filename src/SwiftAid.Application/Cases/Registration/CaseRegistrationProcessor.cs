using System.Collections.Immutable;
using System.Security.Cryptography;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases.Registration;

/// <summary>Coordinates authorized staff registration, receipt replay, event creation, and transactional staging.</summary>
public sealed class CaseRegistrationProcessor : IManagedCaseRegistrationProcessor
{
    private const int MaximumAttempts = 3;
    private readonly ICaseRegistrationUnitOfWork _unitOfWork;
    private readonly ICaseRegistrationAuthorization _authorization;
    private readonly ICaseRegistrationPersistence _persistence;

    public CaseRegistrationProcessor(
        ICaseRegistrationUnitOfWork unitOfWork,
        ICaseRegistrationAuthorization authorization,
        ICaseRegistrationPersistence persistence)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    public async ValueTask<CaseRegistrationResult> RegisterAsync(
        ActorContext actor,
        RegisterManagedCaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);

        CanonicalCaseRegistrationSnapshot snapshot;
        try
        {
            snapshot = CanonicalCaseRegistrationHasher.CreateSnapshot(actor, request);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Invalid, request.CommandId);
        }

        if (snapshot.Report is CaseReportEventData.AnimalV2 animalReport &&
            animalReport.Report.Validate().Count > 0)
        {
            return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Invalid, request.CommandId);
        }

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await ProcessAttemptAsync(actor, request, snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (CasePersistenceRaceException) when (attempt < MaximumAttempts)
            {
                // Start from a fresh scope lease and repeat authorization and receipt lookup.
            }
            catch (CaseLeaseDiscoveryChangedException) when (attempt < MaximumAttempts)
            {
                // Discovery changes are retried as complete transactions, never inside a held lease.
            }
            catch (CasePersistenceRaceException)
            {
                return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Unavailable, request.CommandId);
            }
            catch (CaseLeaseDiscoveryChangedException)
            {
                return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Unavailable, request.CommandId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Unavailable, request.CommandId);
            }
        }

        return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Unavailable, request.CommandId);
    }

    private async ValueTask<CaseRegistrationResult> ProcessAttemptAsync(
        ActorContext actor,
        RegisterManagedCaseRequest request,
        CanonicalCaseRegistrationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        Guid? candidateCaseId;
        await using (var lease = await _unitOfWork.BeginCreateAsync(
            actor,
            request.WorkspaceId,
            cancellationToken).ConfigureAwait(false))
        {
            EnsureCreateLease(lease, actor);
            var decision = await _authorization.AuthorizeNewScopeAsync(
                actor,
                lease,
                request,
                cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                await _persistence.StageDeniedAuditAsync(
                    lease,
                    new CaseRegistrationDenialAudit(actor.ActorId, request.CommandId, decision.Code),
                    cancellationToken).ConfigureAwait(false);
                await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
                return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Denied, request.CommandId, true);
            }

            if (string.IsNullOrWhiteSpace(decision.MatchedGrantGroupId))
            {
                return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Unavailable, request.CommandId);
            }

            candidateCaseId = await _persistence.FindReceiptCandidateCaseIdAsync(
                lease,
                actor.ActorId,
                request.CommandId,
                cancellationToken).ConfigureAwait(false);
            if (candidateCaseId is null)
            {
                return await CreateNewCaseAsync(
                    actor,
                    snapshot,
                    lease,
                    decision.MatchedGrantGroupId,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return await ReplayAuthorizedReceiptAsync(actor, snapshot, candidateCaseId.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<CaseRegistrationResult> ReplayAuthorizedReceiptAsync(
        ActorContext actor,
        CanonicalCaseRegistrationSnapshot snapshot,
        Guid candidateCaseId,
        CancellationToken cancellationToken)
    {
        await using var lease = await _unitOfWork.BeginExistingCaseReplayAsync(
            actor,
            candidateCaseId,
            cancellationToken).ConfigureAwait(false);
        EnsureReplayLease(lease, candidateCaseId, actor);

        var decision = await _authorization.AuthorizeExistingCaseReplayAsync(
            actor,
            lease,
            cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await _persistence.StageDeniedAuditAsync(
                lease,
                new CaseRegistrationDenialAudit(actor.ActorId, snapshot.CommandId, decision.Code),
                cancellationToken).ConfigureAwait(false);
            await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Denied, snapshot.CommandId, true);
        }

        if (string.IsNullOrWhiteSpace(decision.MatchedGrantGroupId))
        {
            return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Unavailable, snapshot.CommandId);
        }

        var receipt = await _persistence.FindAuthorizedReceiptAsync(
            lease,
            actor.ActorId,
            snapshot.CommandId,
            cancellationToken).ConfigureAwait(false);
        if (receipt is null || receipt.CaseId != candidateCaseId)
        {
            return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Unavailable, snapshot.CommandId);
        }

        if (!ReceiptMatches(snapshot, receipt))
        {
            return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Conflict, snapshot.CommandId);
        }

        if (receipt.AcceptedEventIds.IsDefaultOrEmpty || receipt.AcceptedEventIds.Any(id => id == Guid.Empty) ||
            receipt.AggregateVersion < 2 || receipt.ReportRevision < 1)
        {
            return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Unavailable, snapshot.CommandId);
        }

        return new CaseRegistrationResult(
            CaseRegistrationStatus.Replayed,
            candidateCaseId,
            snapshot.CommandId,
            receipt.AcceptedEventIds,
            receipt.AggregateVersion,
            receipt.ReportRevision);
    }

    private async ValueTask<CaseRegistrationResult> CreateNewCaseAsync(
        ActorContext actor,
        CanonicalCaseRegistrationSnapshot snapshot,
        ICaseRegistrationLease lease,
        string matchedGrantGroupId,
        CancellationToken cancellationToken)
    {
        var recordedAtUtc = (await _persistence.GetDatabaseUtcNowAsync(lease, cancellationToken)
            .ConfigureAwait(false)).ToUniversalTime();
        var caseId = Guid.NewGuid();
        var events = CaseAggregate.CreateManagedCase(
            caseId,
            snapshot.CommandId,
            snapshot.WorkspaceId,
            snapshot.Report,
            GetOccurredAtUtc(snapshot.Report),
            recordedAtUtc,
            new ActorCapabilities(actor.ActorId, CaseCapability.AcceptReport));
        if (events.Count != 2 || events.Any(item => item.CaseId != caseId || item.CommandId != snapshot.CommandId ||
                !string.Equals(item.ActorId, actor.ActorId, StringComparison.Ordinal) || item.RecordedAtUtc != recordedAtUtc) ||
            events[0].AggregateVersion != 1 || events[1].AggregateVersion != 2 ||
            events[0].ReportRevision != 1 || events[1].ReportRevision != 1)
        {
            return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Unavailable, snapshot.CommandId);
        }

        var append = await _persistence.StageAcceptedRegistrationAsync(
            lease,
            new CaseRegistrationAppendRequest(
                snapshot,
                matchedGrantGroupId,
                caseId,
                actor.AccessEpoch,
                recordedAtUtc,
                events),
            cancellationToken).ConfigureAwait(false);
        switch (append.Status)
        {
            case CaseRegistrationAppendStatus.AuthorizationChanged:
                return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Denied, snapshot.CommandId);
            case CaseRegistrationAppendStatus.CommandKeyConflict:
                throw new CasePersistenceRaceException(CasePersistenceRaceKind.ReceiptKeyConflict);
            case CaseRegistrationAppendStatus.Unavailable:
                return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Unavailable, snapshot.CommandId);
            case CaseRegistrationAppendStatus.Staged:
                var eventIds = events.Select(item => item.EventId).ToImmutableArray();
                if (!append.EventIds.SequenceEqual(eventIds) || append.AggregateVersion != 2 || append.ReportRevision != 1)
                {
                    return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Unavailable, snapshot.CommandId);
                }

                await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new CaseRegistrationResult(
                    CaseRegistrationStatus.Accepted,
                    caseId,
                    snapshot.CommandId,
                    append.EventIds,
                    append.AggregateVersion,
                    append.ReportRevision);
            default:
                return CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Unavailable, snapshot.CommandId);
        }
    }

    private static bool ReceiptMatches(CanonicalCaseRegistrationSnapshot snapshot, CaseRegistrationReceipt receipt)
    {
        if (!string.Equals(receipt.ActorId, snapshot.ActorId, StringComparison.Ordinal) ||
            receipt.CommandId != snapshot.CommandId ||
            !string.Equals(receipt.CommandType, CanonicalCaseRegistrationHasher.CommandType, StringComparison.Ordinal))
        {
            return false;
        }

        if (receipt.ReceiptProtocolVersion != CanonicalCaseRegistrationHasher.CurrentReceiptProtocolVersion ||
            receipt.WireCommandSchemaVersion != CanonicalCaseRegistrationHasher.CurrentWireCommandSchemaVersion)
        {
            throw new NotSupportedException("The stored registration receipt protocol is not supported.");
        }

        var expectedHash = snapshot.CanonicalPayloadHash;
        var actualHash = receipt.CanonicalPayloadHash.IsDefault ? ImmutableArray<byte>.Empty : receipt.CanonicalPayloadHash;
        return expectedHash.Length == actualHash.Length &&
            CryptographicOperations.FixedTimeEquals(expectedHash.AsSpan(), actualHash.AsSpan());
    }

    private static DateTimeOffset? GetOccurredAtUtc(CaseReportEventData report)
    {
        var occurrence = report switch
        {
            CaseReportEventData.HumanV2 human => human.Report.Context.Occurrence,
            CaseReportEventData.AnimalV2 animal => animal.Report.Context.Occurrence,
            _ => throw new NotSupportedException("Only typed reports can create a managed case.")
        };

        if (!occurrence.Value.HasValue) return null;
        return occurrence.Value.Value.Value?.ToUniversalTime();
    }

    private static void EnsureCreateLease(ICaseRegistrationLease lease, ActorContext actor)
    {
        if (!lease.IsActive || lease.Mode != CaseRegistrationLeaseMode.Create || lease.CaseId is not null)
        {
            throw new InvalidOperationException("The registration creation lease is unavailable.");
        }

        if (lease.LockedActorIds.IsDefault || !lease.LockedActorIds.Contains(actor.ActorId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The registration creation lease has no current-actor lock.");
        }
    }

    private static void EnsureReplayLease(ICaseRegistrationLease lease, Guid caseId, ActorContext actor)
    {
        if (!lease.IsActive || lease.Mode != CaseRegistrationLeaseMode.ExistingCaseReplay || lease.CaseId != caseId)
        {
            throw new InvalidOperationException("The authorized receipt replay lease is unavailable.");
        }

        if (lease.LockedActorIds.IsDefault || !lease.LockedActorIds.Contains(actor.ActorId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The authorized receipt replay lease has no current-actor lock.");
        }
    }

}
