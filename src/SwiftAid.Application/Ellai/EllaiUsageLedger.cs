namespace SwiftAid.Application.Ellai;

/// <summary>One provider-reported usage sample for a reasoning call.
/// Caller maps the adapter result; the ledger never touches credentials.</summary>
public sealed record EllaiUsageSample(
    string SessionId,
    int PromptTokens,
    int CompletionTokens);

/// <summary>Accumulated usage totals for one session envelope.</summary>
public sealed record EllaiUsageTotals(
    string SessionId,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int Calls);

/// <summary>Per-session usage ledger with a hard token budget
/// (EL-MODEL-06, EL-LOOP-04). Until operator pricing is configured the
/// $150/envelope reservation is enforced as a token cap; reservation
/// failures deny further model calls instead of silently overspending.
/// Thread-safe; sessions are isolated by session id.</summary>
public sealed class EllaiUsageLedger(int sessionTokenBudget = EllaiUsageLedger.DefaultSessionTokenBudget)
{
    public const int DefaultSessionTokenBudget = 200_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, EllaiUsageTotals> _totals = new(StringComparer.Ordinal);

    public int SessionTokenBudget { get; } = sessionTokenBudget <= 0
        ? throw new ArgumentOutOfRangeException(nameof(sessionTokenBudget))
        : sessionTokenBudget;

    /// <summary>Reserves estimated tokens before a model call; false means
    /// the envelope budget is exhausted and the call must not be made.</summary>
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
            return used + estimatedTokens <= SessionTokenBudget;
        }
    }

    public EllaiUsageTotals Record(EllaiUsageSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentException.ThrowIfNullOrWhiteSpace(sample.SessionId);
        if (sample.PromptTokens < 0 || sample.CompletionTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sample));
        }

        lock (_gate)
        {
            _totals.TryGetValue(sample.SessionId, out var current);
            var next = new EllaiUsageTotals(
                sample.SessionId,
                (current?.PromptTokens ?? 0) + sample.PromptTokens,
                (current?.CompletionTokens ?? 0) + sample.CompletionTokens,
                (current?.TotalTokens ?? 0) + sample.PromptTokens + sample.CompletionTokens,
                (current?.Calls ?? 0) + 1);
            _totals[sample.SessionId] = next;
            return next;
        }
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
