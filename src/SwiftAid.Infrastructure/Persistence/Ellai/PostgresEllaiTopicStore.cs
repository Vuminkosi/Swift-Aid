using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

/// <summary>PostgreSQL-backed topic-history authority. Record is an upsert
/// on the stable topic id. Concurrent stale writes fail rather than replacing
/// a result that another request has already stored.</summary>
public sealed class PostgresEllaiTopicStore(SwiftAidDbContext dbContext) : IEllaiTopicStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    internal bool UsesContext(SwiftAidDbContext dbContext) => ReferenceEquals(_dbContext, dbContext);

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
            return await UpdateAsync(existing).ConfigureAwait(false);
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
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_ellai_topics" })
        {
            _dbContext.Entry(entity).State = EntityState.Detached;
            throw new DbUpdateConcurrencyException("The topic was created by another operation.", error);
        }

        async Task<EllaiTopicEntry> UpdateAsync(EllaiTopicEntity row)
        {
            if (!string.Equals(row.SessionId, sessionId, StringComparison.Ordinal) ||
                !string.Equals(row.Title, title, StringComparison.Ordinal))
                throw new InvalidOperationException("The topic identifier belongs to a different session or title.");
            row.AliasesJson = JsonSerializer.Serialize(aliases);
            row.EntityRef = entityRef;
            row.EntityVersion = entityVersion;
            row.CheckpointId = checkpointId ?? row.CheckpointId;
            row.UpdatedAtUtc = now;
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToEntry(row);
        }
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

    private static EllaiTopicEntry ToEntry(EllaiTopicEntity entity) => new(
        entity.TopicId, entity.SessionId, entity.Title,
        JsonSerializer.Deserialize<List<string>>(entity.AliasesJson) ?? new List<string>(),
        entity.EntityRef, entity.EntityVersion, entity.CoverageState, entity.CheckpointId,
        entity.CreatedAtUtc, entity.UpdatedAtUtc);
}
