namespace SwiftAid.Application.Ellai;

/// <summary>Durable usage sample: provider-reported tokens plus the priced
/// dollar cost at record time. Dollar math uses the ledger's active price
/// table; unpriced rows carry zero and token budgets stay authoritative.</summary>
public sealed record StoredUsageSample(
    string SessionId,
    int PromptTokens,
    int CompletionTokens,
    decimal Dollars,
    DateTimeOffset AtUtc)
{
    // Estimates are stored separately from provider-reported usage. They are
    // budget reservations, not measurements or a provider invoice.
    public int EstimatedPromptTokens { get; init; }
    public int EstimatedCompletionTokens { get; init; }
    public decimal EstimatedDollars { get; init; }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionId);
        if (PromptTokens < 0 || CompletionTokens < 0 || Dollars < 0 ||
            EstimatedPromptTokens < 0 || EstimatedCompletionTokens < 0 || EstimatedDollars < 0)
            throw new ArgumentOutOfRangeException(nameof(StoredUsageSample), "Usage and estimates must be nonnegative.");
    }
}

/// <summary>Durable usage-sample port. PostgreSQL is the production
/// implementation; totals derive from row sums so a restarted process and a
/// sibling instance converge on the same spend.</summary>
public interface IEllaiUsageStore
{
    Task RecordAsync(StoredUsageSample sample, CancellationToken ct);

    Task<EllaiUsageTotals> TotalsAsync(string sessionId, CancellationToken ct);

    Task<decimal> SpendForAsync(string sessionId, CancellationToken ct);

    Task<decimal> TotalSpendAsync(CancellationToken ct);

    Task<decimal> BudgetSpendForAsync(string sessionId, CancellationToken ct);

    Task<decimal> TotalBudgetSpendAsync(CancellationToken ct);
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
        sample.Validate();
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
                (int)Math.Min(int.MaxValue, rows.Sum(s => (long)s.PromptTokens)),
                (int)Math.Min(int.MaxValue, rows.Sum(s => (long)s.CompletionTokens)),
                (int)Math.Min(int.MaxValue, rows.Sum(s => (long)s.PromptTokens + s.CompletionTokens + s.EstimatedPromptTokens + s.EstimatedCompletionTokens)),
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

    public Task<decimal> BudgetSpendForAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_samples.Where(s => s.SessionId == sessionId).Sum(s => s.Dollars + s.EstimatedDollars));
    }

    public Task<decimal> TotalBudgetSpendAsync(CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_samples.Sum(s => s.Dollars + s.EstimatedDollars));
    }
}
