using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

/// <summary>PostgreSQL-backed Ellai session store (ELL-T04). Versioned
/// compare-and-set via the fencing concurrency token: a stale expected
/// version yields null instead of overwriting newer state. Checkpoints are
/// idempotent by checkpoint ID. Provider calls never run inside these
/// transactions; callers keep them short.</summary>
public sealed class PostgresEllaiSessionStore(SwiftAidDbContext dbContext) : IEllaiSessionStore, IEllaiUtteranceOutboxStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<IReadOnlyList<EllaiSessionRecord>> ListOwnedAsync(string ownerActorId, int offset, int take, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerActorId);
        var rows = await _dbContext.Set<EllaiSessionEntity>().AsNoTracking()
            .Where(session => session.OwnerActorId == ownerActorId)
            .OrderByDescending(session => session.UpdatedAtUtc).ThenBy(session => session.SessionId)
            .Skip(Math.Max(0, offset)).Take(Math.Clamp(take, 1, 101))
            .Select(session => new
            {
                Session = session,
                Preview = _dbContext.Set<EllaiUtteranceEntity>()
                    .Where(turn => turn.SessionId == session.SessionId && turn.UserText != null && turn.UserText != "")
                    .OrderBy(turn => turn.Sequence).ThenBy(turn => turn.ClientUtteranceId)
                    .Select(turn => turn.UserText!.Substring(0, 160)).FirstOrDefault(),
                TurnCount = _dbContext.Set<EllaiUtteranceEntity>().Count(turn => turn.SessionId == session.SessionId)
            })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(row => ToRecord(row.Session) with
        {
            HistoryPreview = row.Preview,
            HistoryTurnCount = row.TurnCount
        }).ToArray();
    }

    public async Task<EllaiSessionRecord?> GetAsync(string sessionId, CancellationToken ct)
    {
        var entity = await _dbContext.Set<EllaiSessionEntity>().AsNoTracking()
            .SingleOrDefaultAsync(e => e.SessionId == sessionId, ct)
            .ConfigureAwait(false);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<EllaiSessionRecord> CreateAsync(EllaiSessionRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        _dbContext.Set<EllaiSessionEntity>().Add(ToEntity(record));
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return record;
    }

    public async Task<EllaiSessionRecord?> TryUpdateAsync(EllaiSessionRecord record, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var tracked = _dbContext.Set<EllaiSessionEntity>().Local
            .FirstOrDefault(e => e.SessionId == record.SessionId);
        if (tracked is not null)
        {
            if (tracked.Version != expectedVersion)
            {
                return null;
            }

            Apply(record, tracked);
            tracked.Version = record.SessionVersion;
            try
            {
                await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                return null;
            }

            return record;
        }

        var entity = new EllaiSessionEntity { SessionId = record.SessionId };
        _dbContext.Attach(entity);
        Apply(record, entity);
        entity.Version = record.SessionVersion;
        _dbContext.Entry(entity).Property(e => e.Version).OriginalValue = expectedVersion;

        try
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.Entry(entity).State = EntityState.Detached;
            return null;
        }

        return record;
    }

    public async Task<EllaiUtteranceStoreResult?> TryAcceptUtteranceAsync(
        EllaiSessionRecord nextRecord,
        long expectedVersion,
        string clientUtteranceId,
        CancellationToken ct,
        string? outboxPayloadJson = null)
    {
        ArgumentNullException.ThrowIfNull(nextRecord);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientUtteranceId);
        if (outboxPayloadJson is { } payload
            && System.Text.Encoding.UTF8.GetByteCount(payload) > EllaiUtteranceEntityConfiguration.MaximumWorkPayloadBytes)
        {
            throw new ArgumentException("The persisted Ellai work payload exceeds its storage bound.", nameof(outboxPayloadJson));
        }
        if (string.IsNullOrWhiteSpace(nextRecord.SessionId)
            || expectedVersion < 1)
        {
            throw new ArgumentException("The accepted session identity or expected version is invalid.", nameof(nextRecord));
        }

        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        var utterances = _dbContext.Set<EllaiUtteranceEntity>();
        var alreadyAccepted = await utterances.AsNoTracking()
            .AnyAsync(
                entity => entity.SessionId == nextRecord.SessionId
                    && entity.ClientUtteranceId == clientUtteranceId,
                ct)
            .ConfigureAwait(false);
        if (alreadyAccepted)
        {
            var replayRecord = await LoadSessionAsync(nextRecord.SessionId, ct).ConfigureAwait(false);
            if (replayRecord is null
                || replayRecord.OwnerActorId is null
                || !string.Equals(replayRecord.OwnerActorId, nextRecord.OwnerActorId, StringComparison.Ordinal))
            {
                return null;
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new EllaiUtteranceStoreResult(replayRecord, Replay: true);
        }

        var currentEntity = await _dbContext.Set<EllaiSessionEntity>().AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.SessionId == nextRecord.SessionId, ct)
            .ConfigureAwait(false);
        if (currentEntity is null || currentEntity.Version != expectedVersion)
        {
            return null;
        }

        if (currentEntity.Version == long.MaxValue
            || nextRecord.SessionVersion != currentEntity.Version + 1)
        {
            throw new ArgumentException("The accepted session version is invalid.", nameof(nextRecord));
        }

        if (currentEntity.Status is "Cancelled" or "Completed"
            || currentEntity.CyclesUsed >= currentEntity.CyclesAllowed
            || nextRecord.CyclesUsed != currentEntity.CyclesUsed + 1
            || nextRecord.CyclesAllowed != currentEntity.CyclesAllowed
            || currentEntity.OwnerActorId is null
            || !string.Equals(currentEntity.OwnerActorId, nextRecord.OwnerActorId, StringComparison.Ordinal))
        {
            return null;
        }

        var localEntity = _dbContext.Set<EllaiSessionEntity>().Local
            .FirstOrDefault(entity => entity.SessionId == nextRecord.SessionId);
        var sessionEntity = localEntity ?? new EllaiSessionEntity { SessionId = nextRecord.SessionId };
        var wasTracked = localEntity is not null;
        var previousState = wasTracked ? _dbContext.Entry(sessionEntity).State : EntityState.Detached;
        var previousCurrentValues = wasTracked
            ? _dbContext.Entry(sessionEntity).CurrentValues.Clone()
            : null;
        var previousOriginalValues = wasTracked
            ? _dbContext.Entry(sessionEntity).OriginalValues.Clone()
            : null;

        if (!wasTracked)
        {
            _dbContext.Attach(sessionEntity);
        }

        Apply(nextRecord, sessionEntity);
        sessionEntity.Version = nextRecord.SessionVersion;
        var sessionEntry = _dbContext.Entry(sessionEntity);
        sessionEntry.Property(entity => entity.Version).OriginalValue = expectedVersion;

        var utteranceEntity = new EllaiUtteranceEntity
        {
            SessionId = nextRecord.SessionId,
            ClientUtteranceId = clientUtteranceId,
            AcceptedAtUtc = DateTimeOffset.UtcNow,
            WorkPayloadJson = outboxPayloadJson,
            Sequence = nextRecord.SessionVersion,
            UserText = outboxPayloadJson is null ? null : EllaiUtteranceWorkEnvelope.Deserialize(outboxPayloadJson).Utterance,
        };
        utterances.Add(utteranceEntity);

        try
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new EllaiUtteranceStoreResult(nextRecord, Replay: false);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
            RestoreTrackingState(
                sessionEntity,
                wasTracked,
                previousState,
                previousCurrentValues,
                previousOriginalValues,
                utteranceEntity);

            var acceptedByConcurrentRequest = await utterances.AsNoTracking()
                .AnyAsync(
                    entity => entity.SessionId == nextRecord.SessionId
                        && entity.ClientUtteranceId == clientUtteranceId,
                    ct)
                .ConfigureAwait(false);
            if (!acceptedByConcurrentRequest)
            {
                return null;
            }

            var replayRecord = await LoadSessionAsync(nextRecord.SessionId, ct).ConfigureAwait(false);
            if (replayRecord is null
                || replayRecord.OwnerActorId is null
                || !string.Equals(replayRecord.OwnerActorId, nextRecord.OwnerActorId, StringComparison.Ordinal))
            {
                return null;
            }

            return new EllaiUtteranceStoreResult(replayRecord, Replay: true);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
            RestoreTrackingState(
                sessionEntity,
                wasTracked,
                previousState,
                previousCurrentValues,
                previousOriginalValues,
                utteranceEntity);

            var acceptedByConcurrentRequest = await utterances.AsNoTracking()
                .AnyAsync(
                    entity => entity.SessionId == nextRecord.SessionId
                        && entity.ClientUtteranceId == clientUtteranceId,
                    ct)
                .ConfigureAwait(false);
            if (!acceptedByConcurrentRequest)
            {
                throw;
            }

            var replayRecord = await LoadSessionAsync(nextRecord.SessionId, ct).ConfigureAwait(false);
            if (replayRecord is null
                || replayRecord.OwnerActorId is null
                || !string.Equals(replayRecord.OwnerActorId, nextRecord.OwnerActorId, StringComparison.Ordinal))
            {
                return null;
            }

            return new EllaiUtteranceStoreResult(replayRecord, Replay: true);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
            RestoreTrackingState(
                sessionEntity,
                wasTracked,
                previousState,
                previousCurrentValues,
                previousOriginalValues,
                utteranceEntity);
            throw;
        }
    }

    public async Task<EllaiUtteranceOutboxItem?> ClaimNextAsync(
        DateTimeOffset nowUtc, DateTimeOffset leaseExpiresAtUtc, CancellationToken ct)
    {
        if (leaseExpiresAtUtc <= nowUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAtUtc), "The outbox lease must expire after it is claimed.");
        }

        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ellai_utterances
            SET work_payload_json = NULL,
                lease_expires_at_utc = NULL,
                completed_at_utc = {nowUtc},
                outcome = 'TerminalFailure'
            WHERE work_payload_json IS NOT NULL
              AND completed_at_utc IS NULL
              AND attempt_count >= 3
              AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= {nowUtc})
            """, ct).ConfigureAwait(false);

        var candidates = await _dbContext.Set<EllaiUtteranceEntity>()
            .FromSqlInterpolated($"""
                SELECT candidate.* FROM ellai_utterances AS candidate
                WHERE candidate.work_payload_json IS NOT NULL
                  AND candidate.completed_at_utc IS NULL
                  AND (candidate.lease_expires_at_utc IS NULL OR candidate.lease_expires_at_utc <= {nowUtc})
                  AND candidate.attempt_count < 3
                  AND NOT EXISTS (
                      SELECT 1 FROM ellai_utterances AS prior
                      WHERE prior.session_id = candidate.session_id
                        AND prior.work_payload_json IS NOT NULL
                        AND prior.completed_at_utc IS NULL
                        AND (prior.accepted_at_utc, prior.client_utterance_id)
                            < (candidate.accepted_at_utc, candidate.client_utterance_id))
                  AND NOT EXISTS (
                      SELECT 1 FROM ellai_utterances AS active
                      WHERE active.session_id = candidate.session_id
                        AND active.work_payload_json IS NOT NULL
                        AND active.completed_at_utc IS NULL
                        AND active.lease_expires_at_utc > {nowUtc})
                ORDER BY candidate.accepted_at_utc, candidate.session_id, candidate.client_utterance_id
                LIMIT 1 FOR UPDATE OF candidate SKIP LOCKED
                """)
            .AsNoTracking().ToListAsync(ct)
            .ConfigureAwait(false);
        var candidate = candidates.FirstOrDefault();
        if (candidate is null)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return null;
        }

        candidate.AttemptCount++;
        candidate.Outcome = "Processing";
        candidate.LeaseExpiresAtUtc = leaseExpiresAtUtc;
        await _dbContext.Set<EllaiUtteranceEntity>()
            .Where(turn => turn.SessionId == candidate.SessionId && turn.ClientUtteranceId == candidate.ClientUtteranceId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(turn => turn.AttemptCount, candidate.AttemptCount)
                .SetProperty(turn => turn.Outcome, "Processing")
                .SetProperty(turn => turn.LeaseExpiresAtUtc, leaseExpiresAtUtc), ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new EllaiUtteranceOutboxItem(
            candidate.SessionId,
            candidate.ClientUtteranceId,
            candidate.WorkPayloadJson!,
            candidate.AttemptCount,
            candidate.AcceptedAtUtc);
    }

    public Task<EllaiConversationTurn?> ReadLatestGuidanceTurnAsync(string sessionId, string ownerActorId, CancellationToken ct) =>
        _dbContext.Set<EllaiUtteranceEntity>().AsNoTracking()
            .Where(turn => turn.SessionId == sessionId && turn.Outcome == "Completed" && turn.UiProposalsJson != null &&
                EF.Functions.JsonContains(turn.UiProposalsJson, "[{\"kind\":\"StartGuidance\"}]") &&
                _dbContext.Set<EllaiSessionEntity>().Any(session =>
                    session.SessionId == turn.SessionId && session.OwnerActorId == ownerActorId))
            .OrderByDescending(turn => turn.Sequence).ThenByDescending(turn => turn.ClientUtteranceId)
            .Select(turn => new EllaiConversationTurn(turn.SessionId, turn.ClientUtteranceId,
                turn.Sequence, turn.UserText, turn.AssistantText, turn.Outcome,
                turn.AttemptCount, turn.CompletedAtUtc) { UiProposalsJson = turn.UiProposalsJson, ToolTraceJson = turn.ToolTraceJson })
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<EllaiConversationTurn>> ReadConversationAsync(
        string sessionId, string ownerActorId, CancellationToken ct)
    {
        // Keep the owner predicate in the query, including for completed sessions.
        var rows = await _dbContext.Set<EllaiUtteranceEntity>().AsNoTracking()
            .Where(turn => turn.SessionId == sessionId
                && _dbContext.Set<EllaiSessionEntity>().Any(session =>
                    session.SessionId == turn.SessionId && session.OwnerActorId == ownerActorId))
            .OrderByDescending(turn => turn.Sequence).ThenByDescending(turn => turn.ClientUtteranceId)
            .Take(100)
            .Select(turn => new EllaiConversationTurn(turn.SessionId, turn.ClientUtteranceId,
                turn.Sequence, turn.UserText, turn.AssistantText, turn.Outcome,
                turn.AttemptCount, turn.CompletedAtUtc) { UiProposalsJson = turn.UiProposalsJson, ToolTraceJson = turn.ToolTraceJson })
            .ToListAsync(ct).ConfigureAwait(false);
        rows.Reverse();
        return rows;
    }

    public async Task<EllaiUtteranceResolution?> CompleteAsync(
        string sessionId, string clientUtteranceId, int attemptCount,
        DateTimeOffset completedAtUtc, CancellationToken ct, EllaiUtteranceResolution? resolution = null)
    {
        resolution ??= EllaiUtteranceResolution.Failed;
        resolution.Validate();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        // Serialize against session cancellation/epoch changes. Provider calls never hold this lock.
        var sessions = await _dbContext.Set<EllaiSessionEntity>().FromSqlInterpolated($"""
            SELECT * FROM ellai_sessions WHERE session_id = {sessionId} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var session = sessions.SingleOrDefault();
        if (resolution.ExpectedInteractionEpoch.HasValue && (session is null || session.Status != "Active"
            || session.InteractionEpoch != resolution.ExpectedInteractionEpoch))
        {
            resolution = EllaiUtteranceResolution.Cancelled;
        }

        // Compare the claim attempt in the UPDATE itself: a reclaimed worker cannot acknowledge
        // or overwrite its successor. Response persistence and queue completion are one commit.
        var changed = await _dbContext.Set<EllaiUtteranceEntity>()
            .Where(turn => turn.SessionId == sessionId && turn.ClientUtteranceId == clientUtteranceId
                && turn.AttemptCount == attemptCount && turn.CompletedAtUtc == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(turn => turn.Outcome, resolution.Outcome)
                .SetProperty(turn => turn.AssistantText, resolution.AssistantText)
                .SetProperty(turn => turn.UiProposalsJson, resolution.UiProposalsJson)
                .SetProperty(turn => turn.ToolTraceJson, resolution.ToolTraceJson)
                .SetProperty(turn => turn.WorkPayloadJson, (string?)null)
                .SetProperty(turn => turn.LeaseExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(turn => turn.CompletedAtUtc, completedAtUtc), ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return changed == 1 ? resolution : null;
    }

    public async Task FailAsync(
        string sessionId, string clientUtteranceId, int attemptCount,
        DateTimeOffset retryAtUtc, bool terminal, CancellationToken ct)
    {
        var exhausted = terminal || attemptCount >= 3;
        await _dbContext.Set<EllaiUtteranceEntity>()
            .Where(turn => turn.SessionId == sessionId && turn.ClientUtteranceId == clientUtteranceId
                && turn.AttemptCount == attemptCount && turn.CompletedAtUtc == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(turn => turn.Outcome, exhausted ? "TerminalFailure" : "RetryableFailure")
                .SetProperty(turn => turn.WorkPayloadJson, turn => exhausted ? null : turn.WorkPayloadJson)
                .SetProperty(turn => turn.LeaseExpiresAtUtc, exhausted ? (DateTimeOffset?)null : retryAtUtc)
                .SetProperty(turn => turn.CompletedAtUtc, exhausted ? retryAtUtc : (DateTimeOffset?)null), ct)
            .ConfigureAwait(false);
    }

    public async Task SaveCheckpointAsync(EllaiCheckpoint checkpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var exists = await _dbContext.Set<EllaiCheckpointEntity>().AsNoTracking()
            .AnyAsync(e => e.CheckpointId == checkpoint.CheckpointId, ct)
            .ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        _dbContext.Set<EllaiCheckpointEntity>().Add(new EllaiCheckpointEntity
        {
            CheckpointId = checkpoint.CheckpointId,
            SessionId = checkpoint.SessionId,
            SessionVersion = checkpoint.SessionVersion,
            TurnId = checkpoint.TurnId,
            TopicRefsJson = JsonSerializer.Serialize(checkpoint.TopicRefs),
            PendingReceiptIdsJson = JsonSerializer.Serialize(checkpoint.PendingReceiptIds),
            CreatedAtUtc = checkpoint.CreatedAtUtc,
        });
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static void Apply(EllaiSessionRecord record, EllaiSessionEntity entity)
    {
        entity.Mode = record.Mode.ToString();
        entity.Status = record.Status;
        entity.CyclesUsed = record.CyclesUsed;
        entity.CyclesAllowed = record.CyclesAllowed;
        entity.ControlFence = record.ControlFence;
        entity.InteractionEpoch = record.InteractionEpoch;
        entity.SpeechEpoch = record.SpeechEpoch;
        entity.ScopeEpoch = record.ScopeEpoch;
        entity.OwnerActorId = record.OwnerActorId;
        entity.ActiveInvestigationId = record.ActiveInvestigationId;
        entity.PendingCheckpointId = record.PendingCheckpointId;
        entity.GuidanceJson = record.GuidanceJson;
        entity.UpdatedAtUtc = record.UpdatedAtUtc;
    }

    private static EllaiSessionRecord ToRecord(EllaiSessionEntity entity) => new(
        entity.SessionId,
        Enum.TryParse<EllaiMode>(entity.Mode, out var mode) ? mode : EllaiMode.Ask,
        entity.Status,
        entity.Version,
        entity.ControlFence,
        entity.InteractionEpoch,
        entity.SpeechEpoch,
        entity.CyclesUsed,
        entity.CyclesAllowed,
        entity.OwnerActorId,
        entity.ScopeEpoch,
        entity.ActiveInvestigationId,
        entity.PendingCheckpointId,
        entity.UpdatedAtUtc) { GuidanceJson = entity.GuidanceJson };

    private async Task<EllaiSessionRecord?> LoadSessionAsync(string sessionId, CancellationToken ct)
    {
        var entity = await _dbContext.Set<EllaiSessionEntity>().AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.SessionId == sessionId, ct)
            .ConfigureAwait(false);
        return entity is null ? null : ToRecord(entity);
    }

    private void RestoreTrackingState(
        EllaiSessionEntity sessionEntity,
        bool wasTracked,
        EntityState previousState,
        PropertyValues? previousCurrentValues,
        PropertyValues? previousOriginalValues,
        EllaiUtteranceEntity utteranceEntity)
    {
        _dbContext.Entry(utteranceEntity).State = EntityState.Detached;
        var sessionEntry = _dbContext.Entry(sessionEntity);
        if (!wasTracked)
        {
            sessionEntry.State = EntityState.Detached;
            return;
        }

        if (previousCurrentValues is not null)
        {
            sessionEntry.CurrentValues.SetValues(previousCurrentValues);
        }

        if (previousOriginalValues is not null)
        {
            sessionEntry.OriginalValues.SetValues(previousOriginalValues);
        }

        sessionEntry.State = previousState;
    }

    private static EllaiSessionEntity ToEntity(EllaiSessionRecord record) => new()
    {
        SessionId = record.SessionId,
        Version = record.SessionVersion,
        Mode = record.Mode.ToString(),
        Status = record.Status,
        CyclesUsed = record.CyclesUsed,
        CyclesAllowed = record.CyclesAllowed,
        OwnerActorId = record.OwnerActorId,
        ControlFence = record.ControlFence,
        InteractionEpoch = record.InteractionEpoch,
        SpeechEpoch = record.SpeechEpoch,
        ScopeEpoch = record.ScopeEpoch,
        ActiveInvestigationId = record.ActiveInvestigationId,
        PendingCheckpointId = record.PendingCheckpointId,
        GuidanceJson = record.GuidanceJson,
        UpdatedAtUtc = record.UpdatedAtUtc,
    };
}
