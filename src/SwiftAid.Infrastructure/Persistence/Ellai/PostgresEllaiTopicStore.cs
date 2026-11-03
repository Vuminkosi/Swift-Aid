using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

/// <summary>PostgreSQL-backed topic-history authority. Record is an upsert
/// on the stable topic id: concurrent re-tracks converge on one row, and a
/// unique-violation retry re-reads the winner.</summary>
public sealed class PostgresEllaiTopicStore(SwiftAidDbContext dbContext) : IEllaiTopicStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<EllaiTopicEntry> RecordAsync(
        string sessionId, string title, IReadOnlyList<string> aliases,
        string? entityRef, string? entityVersion, string? checkpointId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(aliases);

        var id = InMemoryEllaiTopicStore.StableTopicId(sessionId, title);
        var existing = await _dbContext.Set<EllaiTopicEntity>()
            .SingleOrDefaultAsync(e => e.TopicId == id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            existing.AliasesJson = JsonSerializer.Serialize(aliases);
            existing.EntityRef = entityRef;
            existing.EntityVersion = entityVersion;
            existing.CheckpointId = checkpointId ?? existing.CheckpointId;
            existing.UpdatedAtUtc = now;
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToEntry(existing);
        }

        var entity = new EllaiTopicEntity
        {
            TopicId = id,
            SessionId = sessionId,
            Title = title,
            AliasesJson = JsonSerializer.Serialize(aliases),
            EntityRef = entityRef,
            EntityVersion = entityVersion,
            CoverageState = "Partial",
            CheckpointId = checkpointId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _dbContext.Set<EllaiTopicEntity>().Add(entity);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToEntry(entity);
        }
        catch (DbUpdateException)
        {
            _dbContext.Entry(entity).State = EntityState.Detached;
        }

        var winner = await _dbContext.Set<EllaiTopicEntity>().AsNoTracking()
            .SingleAsync(e => e.TopicId == id, cancellationToken)
            .ConfigureAwait(false);
        return ToEntry(winner);
    }

    public async Task<IReadOnlyList<EllaiTopicEntry>> ListAsync(string sessionId, CancellationToken cancellationToken)
    {
        var entities = await _dbContext.Set<EllaiTopicEntity>().AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return entities.Select(ToEntry).ToList();
    }

    public async Task<EllaiTopicEntry?> GetAsync(string sessionId, string topicId, CancellationToken ct)
    {
        var row = await _dbContext.Set<EllaiTopicEntity>().AsNoTracking()
            .SingleOrDefaultAsync(topic => topic.SessionId == sessionId && topic.TopicId == topicId, ct).ConfigureAwait(false);
        return row is null ? null : ToEntry(row);
    }

    public async Task<bool> CompareExchangePayloadAsync(string sessionId, string topicId, string? expectedRef,
        string? expectedVersion, string newRef, string newVersion, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicId);
        ArgumentNullException.ThrowIfNull(newRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(newVersion);
        var changed = await _dbContext.Set<EllaiTopicEntity>()
            .Where(topic => topic.SessionId == sessionId && topic.TopicId == topicId &&
                topic.EntityRef == expectedRef && topic.EntityVersion == expectedVersion)
            .ExecuteUpdateAsync(update => update
                .SetProperty(topic => topic.EntityRef, newRef)
                .SetProperty(topic => topic.EntityVersion, newVersion)
                .SetProperty(topic => topic.UpdatedAtUtc, now), ct).ConfigureAwait(false);
        return changed == 1;
    }

    public async Task<bool> TryCreateAsync(string sessionId, string title, IReadOnlyList<string> aliases,
        string entityRef, string entityVersion, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(entityRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityVersion);
        var id = InMemoryEllaiTopicStore.StableTopicId(sessionId, title);
        var aliasesJson = JsonSerializer.Serialize(aliases);
        var changed = await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ellai_topics (topic_id, session_id, title, aliases_json, entity_ref, entity_version,
                coverage_state, checkpoint_id, created_at_utc, updated_at_utc)
            VALUES ({id}, {sessionId}, {title}, {aliasesJson}, {entityRef}, {entityVersion},
                'Partial', NULL, {now}, {now})
            ON CONFLICT (topic_id) DO NOTHING
            """, ct).ConfigureAwait(false);
        return changed == 1;
    }

    private static EllaiTopicEntry ToEntry(EllaiTopicEntity entity) => new(
        entity.TopicId, entity.SessionId, entity.Title,
        JsonSerializer.Deserialize<List<string>>(entity.AliasesJson) ?? new List<string>(),
        entity.EntityRef, entity.EntityVersion, entity.CoverageState, entity.CheckpointId,
        entity.CreatedAtUtc, entity.UpdatedAtUtc);
}
