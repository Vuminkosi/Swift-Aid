using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

/// <summary>PostgreSQL-backed approval/receipt authority. Record is
/// idempotent by idempotency key under concurrency: a unique-violation
/// retry re-reads the winner instead of inserting a second receipt. A
/// conflicting plan/action triple with a different key is a real conflict,
/// matching the in-memory port's rule.</summary>
public sealed class PostgresEllaiApprovalStore(SwiftAidDbContext dbContext) : IEllaiApprovalStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<EllaiReceipt> RecordAsync(
        string sessionId, string actionId, string idempotencyKey,
        string planHash, int planVersion,
        string? approvedRecipient, DateTimeOffset? approvedDueAtUtc,
        DateTimeOffset now, TimeSpan approvalLifetime, string policyHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(planHash);

        var existing = await _dbContext.Set<EllaiPlanApprovalEntity>().AsNoTracking()
            .SingleOrDefaultAsync(e => e.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return ToReceipt(existing);
        }

        var entity = new EllaiPlanApprovalEntity
        {
            ApprovalId = $"rcpt-{Guid.NewGuid():N}",
            SessionId = sessionId,
            ActionId = actionId,
            IdempotencyKey = idempotencyKey,
            PlanHash = planHash,
            PlanVersion = planVersion,
            ApprovedRecipient = approvedRecipient,
            ApprovedDueAtUtc = approvedDueAtUtc,
            Status = nameof(EllaiReceiptStatus.Accepted),
            PolicyHash = policyHash,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(approvalLifetime),
        };
        _dbContext.Set<EllaiPlanApprovalEntity>().Add(entity);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToReceipt(entity);
        }
        catch (DbUpdateException)
        {
            _dbContext.Entry(entity).State = EntityState.Detached;
        }

        var winner = await _dbContext.Set<EllaiPlanApprovalEntity>().AsNoTracking()
            .SingleOrDefaultAsync(e => e.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (winner is not null)
        {
            return ToReceipt(winner);
        }

        throw new InvalidOperationException(
            "A different approved action already binds this plan version and action.");
    }

    public async Task<EllaiReceipt?> GetAsync(string receiptId, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Set<EllaiPlanApprovalEntity>().AsNoTracking()
            .SingleOrDefaultAsync(e => e.ApprovalId == receiptId, cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : ToReceipt(entity);
    }

    public async Task<IReadOnlyList<EllaiReceipt>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var entities = await _dbContext.Set<EllaiPlanApprovalEntity>().AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return entities.Select(ToReceipt).ToList();
    }

    public async Task<EllaiReceipt?> MarkStatusAsync(
        string receiptId, EllaiReceiptStatus status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Set<EllaiPlanApprovalEntity>()
            .SingleOrDefaultAsync(e => e.ApprovalId == receiptId, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return null;
        }

        var effective = status == EllaiReceiptStatus.Applied && now > entity.ExpiresAtUtc
            ? EllaiReceiptStatus.UnknownOutcome
            : status;
        entity.Status = effective.ToString();
        entity.VerifiedAtUtc = now;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToReceipt(entity);
    }

    private static EllaiReceipt ToReceipt(EllaiPlanApprovalEntity entity) => new(
        entity.ApprovalId, entity.SessionId, entity.ActionId, entity.IdempotencyKey,
        ParseStatus(entity.Status), entity.CreatedAtUtc, entity.ExpiresAtUtc, entity.VerifiedAtUtc,
        entity.PolicyHash, entity.ApprovedRecipient, entity.ApprovedDueAtUtc);

    private static EllaiReceiptStatus ParseStatus(string status) =>
        Enum.TryParse<EllaiReceiptStatus>(status, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Unknown stored receipt status '{status}'.");
}
