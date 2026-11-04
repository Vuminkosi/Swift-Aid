using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

/// <summary>PostgreSQL-backed usage-sample store. Recording is insert-only;
/// totals derive from row sums at read time.</summary>
public sealed class PostgresEllaiUsageStore(SwiftAidDbContext dbContext) : IEllaiUsageStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task RecordAsync(StoredUsageSample sample, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sample);
        sample.Validate();
        _dbContext.Set<EllaiUsageSampleEntity>().Add(new EllaiUsageSampleEntity
        {
            SampleId = $"usg-{Guid.NewGuid():N}",
            SessionId = sample.SessionId,
            PromptTokens = sample.PromptTokens,
            CompletionTokens = sample.CompletionTokens,
            Dollars = sample.Dollars,
            AtUtc = sample.AtUtc,
            EstimatedPromptTokens = sample.EstimatedPromptTokens,
            EstimatedCompletionTokens = sample.EstimatedCompletionTokens,
            EstimatedDollars = sample.EstimatedDollars,
        });
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<EllaiUsageTotals> TotalsAsync(string sessionId, CancellationToken ct)
    {
        var rows = await _dbContext.Set<EllaiUsageSampleEntity>().AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return new EllaiUsageTotals(
            sessionId,
            (int)Math.Min(int.MaxValue, rows.Sum(e => (long)e.PromptTokens)),
            (int)Math.Min(int.MaxValue, rows.Sum(e => (long)e.CompletionTokens)),
            (int)Math.Min(int.MaxValue, rows.Sum(e => (long)e.PromptTokens + e.CompletionTokens + e.EstimatedPromptTokens + e.EstimatedCompletionTokens)),
            rows.Count);
    }

    public async Task<decimal> SpendForAsync(string sessionId, CancellationToken ct) =>
        await _dbContext.Set<EllaiUsageSampleEntity>().AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .SumAsync(e => e.Dollars, ct)
            .ConfigureAwait(false);

    public async Task<decimal> TotalSpendAsync(CancellationToken ct) =>
        await _dbContext.Set<EllaiUsageSampleEntity>().AsNoTracking()
            .SumAsync(e => e.Dollars, ct)
            .ConfigureAwait(false);

    public async Task<decimal> BudgetSpendForAsync(string sessionId, CancellationToken ct) =>
        await _dbContext.Set<EllaiUsageSampleEntity>().AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .SumAsync(e => e.Dollars + e.EstimatedDollars, ct).ConfigureAwait(false);

    public async Task<decimal> TotalBudgetSpendAsync(CancellationToken ct) =>
        await _dbContext.Set<EllaiUsageSampleEntity>().AsNoTracking()
            .SumAsync(e => e.Dollars + e.EstimatedDollars, ct).ConfigureAwait(false);
}
