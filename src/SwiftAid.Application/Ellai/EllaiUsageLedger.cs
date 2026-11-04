namespace SwiftAid.Application.Ellai;

/// <summary>One provider-reported usage sample for a reasoning call.
/// Caller maps the adapter result; the ledger never touches credentials.</summary>
public sealed record EllaiUsageSample(
    string SessionId,
    int PromptTokens,
    int CompletionTokens);

/// <summary>Operator-configured rates. Zero means unpriced: dollar caps
/// stay inert and token budgets remain the active guard until the operator
/// sets real rates. No vendor prices are assumed here.</summary>
public sealed record EllaiPriceTable(
    decimal InputPerThousandTokens,
    decimal OutputPerThousandTokens,
    decimal AudioPerMinute)
{
    public static readonly EllaiPriceTable Unpriced = new(0, 0, 0);

    public bool IsPriced => InputPerThousandTokens > 0 || OutputPerThousandTokens > 0 || AudioPerMinute > 0;

    public decimal CostFor(int promptTokens, int completionTokens) =>
        promptTokens / 1000m * InputPerThousandTokens
        + completionTokens / 1000m * OutputPerThousandTokens;

    public decimal CostForAudio(decimal minutes) => minutes * AudioPerMinute;
}

/// <summary>Accumulated usage totals for one session envelope.</summary>
public sealed record EllaiUsageTotals(
    string SessionId,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int Calls);

/// <summary>Thread-safe, session-scoped usage accounting with configurable
/// token and spend thresholds. Callers can enforce them or continue after
/// emitting threshold warnings; the Gateway uses advisory thresholds.</summary>
public sealed class EllaiUsageLedger(
    int sessionTokenBudget = EllaiUsageLedger.DefaultSessionTokenBudget,
    decimal envelopeSpendCap = EllaiUsageLedger.DefaultEnvelopeSpendCap,
    decimal totalSpendCap = EllaiUsageLedger.DefaultTotalSpendCap,
    EllaiPriceTable? prices = null,
    bool enforceLimits = true,
    Action<string>? thresholdWarning = null)
{
    public const int DefaultSessionTokenBudget = 2_000_000;

    public const decimal DefaultEnvelopeSpendCap = 70.00m;

    public const decimal DefaultTotalSpendCap = 70.00m;

    private readonly object _gate = new();
    private readonly Dictionary<string, EllaiUsageTotals> _totals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> _spend = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> _budgetSpend = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _pendingTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> _pendingSpend = new(StringComparer.Ordinal);
    private decimal _totalPendingSpend;
    private decimal _totalSpend;
    private decimal _totalBudgetSpend;

    public int SessionTokenBudget { get; } = sessionTokenBudget <= 0
        ? throw new ArgumentOutOfRangeException(nameof(sessionTokenBudget))
        : sessionTokenBudget;

    public decimal EnvelopeSpendCap { get; } = envelopeSpendCap < 0
        ? throw new ArgumentOutOfRangeException(nameof(envelopeSpendCap))
        : envelopeSpendCap;

    public decimal TotalSpendCap { get; } = totalSpendCap < 0
        ? throw new ArgumentOutOfRangeException(nameof(totalSpendCap))
        : totalSpendCap;

    public EllaiPriceTable Prices { get; } = prices ?? EllaiPriceTable.Unpriced;

    public bool EnforcesLimits => enforceLimits;

    /// <summary>Checks estimated tokens and priced spend against thresholds.
    /// Returns false only when enforcement is enabled and a threshold is exceeded.</summary>
    public bool TryReserve(string sessionId, int estimatedTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (estimatedTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedTokens));
        }

        lock (_gate)
        {
            var used = _totals.TryGetValue(sessionId, out var current) ? current.TotalTokens : 0;
            _pendingTokens.TryGetValue(sessionId, out var pendingTokens);
            if ((long)used + pendingTokens + estimatedTokens > SessionTokenBudget)
            {
                if (enforceLimits) return false;
                thresholdWarning?.Invoke("session_tokens");
            }

            if (!Prices.IsPriced)
            {
                return true;
            }

            var estimate = Prices.CostFor(estimatedTokens, 0);
            var sessionSpend = _budgetSpend.TryGetValue(sessionId, out var spent) ? spent : 0;
            _pendingSpend.TryGetValue(sessionId, out var pendingSpend);
            var withinThreshold = sessionSpend + pendingSpend + estimate <= EnvelopeSpendCap
                && _totalBudgetSpend + _totalPendingSpend + estimate <= TotalSpendCap;
            if (!withinThreshold && !enforceLimits) thresholdWarning?.Invoke("spend");
            return withinThreshold || !enforceLimits;
        }
    }

    /// <summary>Reserves capacity for one in-process framework model call until disposed.
    /// This does not coordinate reservations across Gateway processes.</summary>
    public IDisposable? TryBeginModelCall(string sessionId, int estimatedTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(estimatedTokens);
        lock (_gate)
        {
            _totals.TryGetValue(sessionId, out var current);
            _pendingTokens.TryGetValue(sessionId, out var pendingTokens);
            if ((long)(current?.TotalTokens ?? 0) + pendingTokens + estimatedTokens > SessionTokenBudget)
            {
                if (enforceLimits) return null;
                thresholdWarning?.Invoke("session_tokens");
            }
            var estimatedSpend = Prices.IsPriced
                ? Math.Max(Prices.CostFor(estimatedTokens, 0), Prices.CostFor(0, estimatedTokens)) : 0;
            _budgetSpend.TryGetValue(sessionId, out var spent);
            _pendingSpend.TryGetValue(sessionId, out var pendingSpend);
            if (Prices.IsPriced && (spent + pendingSpend + estimatedSpend > EnvelopeSpendCap ||
                _totalBudgetSpend + _totalPendingSpend + estimatedSpend > TotalSpendCap))
            {
                if (enforceLimits) return null;
                thresholdWarning?.Invoke("spend");
            }
            _pendingTokens[sessionId] = pendingTokens + estimatedTokens;
            _pendingSpend[sessionId] = pendingSpend + estimatedSpend;
            _totalPendingSpend += estimatedSpend;
            return new ModelCallReservation(this, sessionId, estimatedTokens, estimatedSpend);
        }
    }

    private sealed class ModelCallReservation(EllaiUsageLedger ledger, string sessionId, int tokens, decimal spend) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (ledger._gate)
            {
                var remainingTokens = ledger._pendingTokens[sessionId] - tokens;
                var remainingSpend = ledger._pendingSpend[sessionId] - spend;
                if (remainingTokens == 0) ledger._pendingTokens.Remove(sessionId);
                else ledger._pendingTokens[sessionId] = remainingTokens;
                if (remainingTokens == 0) ledger._pendingSpend.Remove(sessionId);
                else ledger._pendingSpend[sessionId] = remainingSpend;
                ledger._totalPendingSpend -= spend;
            }
        }
    }

    public EllaiUsageTotals Record(EllaiUsageSample sample, int estimatedPromptTokens = 0, int estimatedCompletionTokens = 0)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentException.ThrowIfNullOrWhiteSpace(sample.SessionId);
        if (sample.PromptTokens < 0 || sample.CompletionTokens < 0 || estimatedPromptTokens < 0 || estimatedCompletionTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sample));
        }

        lock (_gate)
        {
            _totals.TryGetValue(sample.SessionId, out var current);
            var next = new EllaiUsageTotals(
                sample.SessionId,
                SaturatingAdd(current?.PromptTokens ?? 0, sample.PromptTokens),
                SaturatingAdd(current?.CompletionTokens ?? 0, sample.CompletionTokens),
                SaturatingAdd(SaturatingAdd(current?.TotalTokens ?? 0, sample.PromptTokens, sample.CompletionTokens),
                    estimatedPromptTokens, estimatedCompletionTokens),
                SaturatingAdd(current?.Calls ?? 0, 1));
            _totals[sample.SessionId] = next;
            if (!enforceLimits && (current?.TotalTokens ?? 0) <= SessionTokenBudget && next.TotalTokens > SessionTokenBudget)
                thresholdWarning?.Invoke("session_tokens");
            if (Prices.IsPriced)
            {
                AccrueAndWarnLocked(sample.SessionId, Prices.CostFor(sample.PromptTokens, sample.CompletionTokens),
                    Prices.CostFor(estimatedPromptTokens, estimatedCompletionTokens));
            }

            return next;
        }
    }

    private static int SaturatingAdd(int first, int second, int third = 0) =>
        (int)Math.Min(int.MaxValue, (long)first + second + third);

    /// <summary>Records billable audio minutes (ASR/TTS) against both caps.</summary>
    public void RecordAudio(string sessionId, decimal minutes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (minutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes));
        }

        lock (_gate)
        {
            if (Prices.IsPriced)
            {
                AccrueAndWarnLocked(sessionId, Prices.CostForAudio(minutes));
            }
        }
    }

    /// <summary>Priced dollar cost of one sample under the active table.
    /// Pure math for durable writes; unpriced tables yield zero.</summary>
    public decimal DollarsFor(EllaiUsageSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return Prices.CostFor(
            Math.Max(0, sample.PromptTokens), Math.Max(0, sample.CompletionTokens));
    }

    /// <summary>Merges durable usage into memory after a restart or from a
    /// sibling instance: counters converge by maximum, never double-count.
    /// Call before budget-gated runs.</summary>
    public async Task HydrateAsync(string sessionId, IEllaiUsageStore store, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(store);
        var totals = await store.TotalsAsync(sessionId, ct).ConfigureAwait(false);
        var sessionSpend = await store.SpendForAsync(sessionId, ct).ConfigureAwait(false);
        var globalSpend = await store.TotalSpendAsync(ct).ConfigureAwait(false);
        var sessionBudgetSpend = await store.BudgetSpendForAsync(sessionId, ct).ConfigureAwait(false);
        var globalBudgetSpend = await store.TotalBudgetSpendAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _totals.TryGetValue(sessionId, out var current);
            _totals[sessionId] = new EllaiUsageTotals(
                sessionId,
                Math.Max(current?.PromptTokens ?? 0, totals.PromptTokens),
                Math.Max(current?.CompletionTokens ?? 0, totals.CompletionTokens),
                Math.Max(current?.TotalTokens ?? 0, totals.TotalTokens),
                Math.Max(current?.Calls ?? 0, totals.Calls));
            _spend.TryGetValue(sessionId, out var spent);
            _spend[sessionId] = Math.Max(spent, sessionSpend);
            _totalSpend = Math.Max(_totalSpend, globalSpend);
            _budgetSpend.TryGetValue(sessionId, out var budgetSpent);
            _budgetSpend[sessionId] = Math.Max(budgetSpent, sessionBudgetSpend);
            _totalBudgetSpend = Math.Max(_totalBudgetSpend, globalBudgetSpend);
        }
    }

    public decimal SpendFor(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            return _spend.TryGetValue(sessionId, out var spent) ? spent : 0;
        }
    }

    public decimal TotalSpend
    {
        get
        {
            lock (_gate)
            {
                return _totalSpend;
            }
        }
    }

    private void AccrueLocked(string sessionId, decimal amount, decimal estimate = 0)
    {
        _spend[sessionId] = (_spend.TryGetValue(sessionId, out var spent) ? spent : 0) + amount;
        _totalSpend += amount;
        _budgetSpend[sessionId] = (_budgetSpend.TryGetValue(sessionId, out var budget) ? budget : 0) + amount + estimate;
        _totalBudgetSpend += amount + estimate;
    }

    private void AccrueAndWarnLocked(string sessionId, decimal amount, decimal estimate = 0)
    {
        _budgetSpend.TryGetValue(sessionId, out var previousSessionSpend);
        var previousTotalSpend = _totalBudgetSpend;
        AccrueLocked(sessionId, amount, estimate);
        if (!enforceLimits &&
            ((previousSessionSpend <= EnvelopeSpendCap && _budgetSpend[sessionId] > EnvelopeSpendCap) ||
             (previousTotalSpend <= TotalSpendCap && _totalBudgetSpend > TotalSpendCap)))
            thresholdWarning?.Invoke("spend");
    }

    public EllaiUsageTotals TotalsFor(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            return _totals.TryGetValue(sessionId, out var current)
                ? current
                : new EllaiUsageTotals(sessionId, 0, 0, 0, 0);
        }
    }

    public int RemainingFor(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            var used = _totals.TryGetValue(sessionId, out var current) ? current.TotalTokens : 0;
            return Math.Max(0, SessionTokenBudget - used);
        }
    }
}
