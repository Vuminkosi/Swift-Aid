using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

/// <summary>PostgreSQL-backed consent-grant store. Recording is idempotent by
/// grant id; a concurrent winner means the grant already exists. Rows with
/// an unparseable dimension are skipped on read (fail-closed: the grant
/// stays denied rather than misclassified).</summary>
public sealed class PostgresEllaiConsentStore(SwiftAidDbContext dbContext) : IEllaiConsentStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task RecordAsync(EllaiConsentGrant grant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var exists = await _dbContext.Set<EllaiConsentGrantEntity>().AsNoTracking()
            .AnyAsync(e => e.GrantId == grant.GrantId, ct)
            .ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        var entity = ToEntity(grant);
        _dbContext.Set<EllaiConsentGrantEntity>().Add(entity);
        try
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            _dbContext.Entry(entity).State = EntityState.Detached;
            var winner = await _dbContext.Set<EllaiConsentGrantEntity>().AsNoTracking()
                .AnyAsync(e => e.GrantId == grant.GrantId, ct)
                .ConfigureAwait(false);
            if (!winner)
            {
                throw new InvalidOperationException(
                    "The consent grant failed without a conflicting row.");
            }
        }
    }

    public async Task<IReadOnlyList<EllaiConsentGrant>> ListBySessionAsync(string sessionId, CancellationToken ct)
    {
        var entities = await _dbContext.Set<EllaiConsentGrantEntity>().AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.Version)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var grants = new List<EllaiConsentGrant>(entities.Count);
        foreach (var entity in entities)
        {
            if (!Enum.TryParse<EllaiConsentDimension>(entity.Dimension, out var dimension))
            {
                continue;
            }

            grants.Add(new EllaiConsentGrant(
                entity.GrantId, entity.SessionId, dimension, entity.Purpose,
                entity.DataClass, entity.Version, entity.ExpiresAtUtc, entity.Revoked));
        }

        return grants;
    }

    public async Task<bool> SetRevokedAsync(string sessionId, string grantId, CancellationToken ct)
    {
        var entity = await _dbContext.Set<EllaiConsentGrantEntity>()
            .SingleOrDefaultAsync(e => e.GrantId == grantId && e.SessionId == sessionId, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        entity.Revoked = true;
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static EllaiConsentGrantEntity ToEntity(EllaiConsentGrant grant) => new()
    {
        GrantId = grant.GrantId,
        SessionId = grant.SessionId,
        Dimension = grant.Dimension.ToString(),
        Purpose = grant.Purpose,
        DataClass = grant.DataClass,
        Version = grant.Version,
        ExpiresAtUtc = grant.ExpiresAtUtc,
        Revoked = grant.Revoked,
        GrantedAtUtc = DateTimeOffset.UtcNow,
    };
}
