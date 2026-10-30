using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

/// <summary>PostgreSQL-backed Ellai session store (ELL-T04). Versioned
/// compare-and-set via the fencing concurrency token: a stale expected
/// version yields null instead of overwriting newer state. Checkpoints are
/// idempotent by checkpoint ID. Provider calls never run inside these
/// transactions; callers keep them short.</summary>
public sealed class PostgresEllaiSessionStore(SwiftAidDbContext dbContext) : IEllaiSessionStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

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

    public async Task<bool> TryClaimUtteranceAsync(
        string sessionId, string clientUtteranceId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientUtteranceId);
        var exists = await _dbContext.Set<EllaiUtteranceEntity>().AsNoTracking()
            .AnyAsync(
                e => e.SessionId == sessionId && e.ClientUtteranceId == clientUtteranceId, ct)
            .ConfigureAwait(false);
        if (exists)
        {
            return false;
        }

        var entity = new EllaiUtteranceEntity
        {
            SessionId = sessionId,
            ClientUtteranceId = clientUtteranceId,
            AcceptedAtUtc = now,
        };
        _dbContext.Set<EllaiUtteranceEntity>().Add(entity);
        try
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            _dbContext.Entry(entity).State = EntityState.Detached;
        }

        // A concurrent winner holds the identity: replay, never a second
        // acceptance. Any other failure surfaces by rethrowing below.
        var winner = await _dbContext.Set<EllaiUtteranceEntity>().AsNoTracking()
            .AnyAsync(
                e => e.SessionId == sessionId && e.ClientUtteranceId == clientUtteranceId, ct)
            .ConfigureAwait(false);
        if (winner)
        {
            return false;
        }

        throw new InvalidOperationException(
            "The utterance claim failed without a conflicting acceptance.");
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
        entity.ActiveInvestigationId = record.ActiveInvestigationId;
        entity.PendingCheckpointId = record.PendingCheckpointId;
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
        entity.ScopeEpoch,
        entity.ActiveInvestigationId,
        entity.PendingCheckpointId,
        entity.UpdatedAtUtc);

    private static EllaiSessionEntity ToEntity(EllaiSessionRecord record) => new()
    {
        SessionId = record.SessionId,
        Version = record.SessionVersion,
        Mode = record.Mode.ToString(),
        Status = record.Status,
        CyclesUsed = record.CyclesUsed,
        CyclesAllowed = record.CyclesAllowed,
        ControlFence = record.ControlFence,
        InteractionEpoch = record.InteractionEpoch,
        SpeechEpoch = record.SpeechEpoch,
        ScopeEpoch = record.ScopeEpoch,
        ActiveInvestigationId = record.ActiveInvestigationId,
        PendingCheckpointId = record.PendingCheckpointId,
        UpdatedAtUtc = record.UpdatedAtUtc,
    };
}
