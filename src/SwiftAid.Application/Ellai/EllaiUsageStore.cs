namespace SwiftAid.Application.Ellai;

/// <summary>Durable usage sample: provider-reported tokens plus the priced
/// dollar cost at record time. Dollar math uses the ledger's active price
/// table; unpriced rows carry zero and token budgets stay authoritative.</summary>
public sealed record StoredUsageSample(
    string SessionId,
    int PromptTokens,
    int CompletionTokens,
    decimal Dollars,
    DateTimeOffset AtUtc);

/// <summary>Durable usage-sample port. PostgreSQL is the production
/// implementation; totals derive from row sums so a restarted process and a
/// sibling instance converge on the same spend.</summary>
public interface IEllaiUsageStore
{
    Task RecordAsync(StoredUsageSample sample, CancellationToken ct);

    Task<EllaiUsageTotals> TotalsAsync(string sessionId, CancellationToken ct);

    Task<decimal> SpendForAsync(string sessionId, CancellationToken ct);

    Task<decimal> TotalSpendAsync(CancellationToken ct);
}

/// <summary>Thread-safe in-memory usage store for tests and local
/// development without a database.</summary>
public sealed class InMemoryEllaiUsageStore : IEllaiUsageStore
{
    private readonly object _gate = new();
    private readonly List<StoredUsageSample> _samples = new();

    public Task RecordAsync(StoredUsageSample sample, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            _samples.Add(sample);
        }

        return Task.CompletedTask;
    }

    public Task<EllaiUsageTotals> TotalsAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            var rows = _samples.Where(s => s.SessionId == sessionId).ToList();
            return Task.FromResult(new EllaiUsageTotals(
                sessionId,
                rows.Sum(s => s.PromptTokens),
                rows.Sum(s => s.CompletionTokens),
                rows.Sum(s => s.PromptTokens + s.CompletionTokens),
                rows.Count));
        }
    }

    public Task<decimal> SpendForAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_samples.Where(s => s.SessionId == sessionId).Sum(s => s.Dollars));
        }
    }

    public Task<decimal> TotalSpendAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_samples.Sum(s => s.Dollars));
        }
    }
}
