using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T20")]
public sealed class EllaiUsageLedgerTests
{
    [Fact]
    public void ActualUsageAndAudioWarnWhenTheyCrossAdvisoryThresholds()
    {
        var warnings = new List<string>();
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 10, envelopeSpendCap: 1m, totalSpendCap: 10m,
            prices: new EllaiPriceTable(1m, 1m, 1m), enforceLimits: false, thresholdWarning: warnings.Add);

        ledger.Record(new EllaiUsageSample("session", 7, 4));
        Assert.Contains("session_tokens", warnings);
        Assert.DoesNotContain("spend", warnings);
        ledger.RecordAudio("session", 1m);
        Assert.Contains("spend", warnings);
        Assert.Equal(11, ledger.TotalsFor("session").TotalTokens);
    }

    [Fact]
    public void AdvisoryThresholdsWarnWithoutBlockingAndKeepUsage()
    {
        var warnings = new List<string>();
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 10, envelopeSpendCap: 0, totalSpendCap: 0,
            prices: new EllaiPriceTable(1, 1, 0), enforceLimits: false, thresholdWarning: warnings.Add);
        using var reservation = ledger.TryBeginModelCall("session", 20);
        Assert.NotNull(reservation);
        Assert.Contains("session_tokens", warnings);
        Assert.Contains("spend", warnings);
        ledger.Record(new EllaiUsageSample("session", 12, 3));
        Assert.Equal(15, ledger.TotalsFor("session").TotalTokens);
    }

    [Fact]
    public void DefaultSessionAllowsTwoMillionTokensAndStillStopsAtTheBoundary()
    {
        var ledger = new EllaiUsageLedger();
        ledger.Record(new EllaiUsageSample("long-session", 160_050, 8_931));
        Assert.True(ledger.TryReserve("long-session", 40_000));
        Assert.Equal(2_000_000, ledger.SessionTokenBudget);
        ledger.Record(new EllaiUsageSample("long-session", 1_831_018, 0));
        Assert.True(ledger.TryReserve("long-session", 1));
        Assert.False(ledger.TryReserve("long-session", 2));
    }

    [Fact]
    public void ReserveFailsClosedWhenBudgetExhausted()
    {
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 100);

        Assert.True(ledger.TryReserve("s-1", 100));
        ledger.Record(new EllaiUsageSample("s-1", 60, 40));

        Assert.False(ledger.TryReserve("s-1", 1));
        Assert.Equal(0, ledger.RemainingFor("s-1"));
    }

    [Fact]
    public void FrameworkReservationsBoundConcurrentSessionAndGlobalCapacity()
    {
        var prices = new EllaiPriceTable(1m, 2m, 0m);
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 100, envelopeSpendCap: 1m,
            totalSpendCap: 0.2m, prices: prices);
        var first = ledger.TryBeginModelCall("s-1", 60);
        Assert.NotNull(first);
        Assert.Null(ledger.TryBeginModelCall("s-1", 41));
        Assert.Null(ledger.TryBeginModelCall("s-2", 41));
        Assert.False(ledger.TryReserve("s-2", 100));
        first.Dispose();
        first.Dispose();
        using var next = ledger.TryBeginModelCall("s-2", 100);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task ConcurrentFrameworkReservationsCannotExceedSessionBudget()
    {
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 100);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 40).Select(async _ =>
        {
            await start.Task;
            return ledger.TryBeginModelCall("s-1", 10);
        }).ToArray();
        start.SetResult();
        var reservations = await Task.WhenAll(attempts);
        Assert.Equal(10, reservations.Count(item => item is not null));
        foreach (var reservation in reservations) reservation?.Dispose();
        using var fullBudget = ledger.TryBeginModelCall("s-1", 100);
        Assert.NotNull(fullBudget);
    }

    [Fact]
    public void FrameworkReservationStaysHeldUntilUsageHasBeenRecorded()
    {
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 100);
        var pending = ledger.TryBeginModelCall("s-1", 80);
        Assert.NotNull(pending);
        ledger.Record(new EllaiUsageSample("s-1", 50, 20));
        Assert.Null(ledger.TryBeginModelCall("s-1", 1));
        pending.Dispose();
        using var remaining = ledger.TryBeginModelCall("s-1", 30);
        Assert.NotNull(remaining);
        Assert.False(ledger.TryReserve("s-1", 1));
    }

    [Fact]
    public async Task LargeUsageAndEstimatesNeverWrapAndBypassBudgetAfterHydration()
    {
        var ledger = new EllaiUsageLedger();
        var store = new InMemoryEllaiUsageStore();
        ledger.Record(new EllaiUsageSample("s-1", 1, 0));
        Assert.False(ledger.TryReserve("s-1", int.MaxValue));

        var sample = new EllaiUsageSample("s-1", int.MaxValue, int.MaxValue);
        var totals = ledger.Record(sample);
        Assert.Equal(int.MaxValue, totals.PromptTokens);
        Assert.Equal(int.MaxValue, totals.TotalTokens);
        Assert.False(ledger.TryReserve("s-1", 1));

        await store.RecordAsync(new StoredUsageSample("s-1", sample.PromptTokens, sample.CompletionTokens, 0, DateTimeOffset.UtcNow), default);
        var restored = new EllaiUsageLedger();
        await restored.HydrateAsync("s-1", store, default);
        Assert.Equal(int.MaxValue, restored.TotalsFor("s-1").TotalTokens);
        Assert.False(restored.TryReserve("s-1", 1));
    }

    [Fact]
    public void TotalsAccumulatePerSessionInIsolation()
    {
        var ledger = new EllaiUsageLedger();

        ledger.Record(new EllaiUsageSample("s-1", 10, 5));
        var totals = ledger.Record(new EllaiUsageSample("s-1", 20, 10));
        var other = ledger.TotalsFor("s-2");

        Assert.Equal(30, totals.PromptTokens);
        Assert.Equal(15, totals.CompletionTokens);
        Assert.Equal(45, totals.TotalTokens);
        Assert.Equal(2, totals.Calls);
        Assert.Equal(0, other.TotalTokens);
        Assert.True(ledger.TryReserve("s-2", 1000));
    }

    [Fact]
    public void NegativeSamplesAreRejected()
    {
        var ledger = new EllaiUsageLedger();

        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Record(new EllaiUsageSample("s-1", -1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.TryReserve("s-1", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EllaiUsageLedger(0));
    }

    [Fact]
    public void DefaultsFundSeventyDollarsUnpriced()
    {
        var ledger = new EllaiUsageLedger();

        Assert.Equal(70.00m, EllaiUsageLedger.DefaultEnvelopeSpendCap);
        Assert.Equal(70.00m, EllaiUsageLedger.DefaultTotalSpendCap);
        Assert.False(ledger.Prices.IsPriced);
        Assert.Equal(0, ledger.TotalSpend);
    }

    [Fact]
    public void PricedSpendTripsEnvelopeAndGlobalCaps()
    {
        var prices = new EllaiPriceTable(InputPerThousandTokens: 1.00m, OutputPerThousandTokens: 2.00m, AudioPerMinute: 0.50m);
        var ledger = new EllaiUsageLedger(
            sessionTokenBudget: int.MaxValue, envelopeSpendCap: 5.00m, totalSpendCap: 6.00m, prices: prices);

        ledger.Record(new EllaiUsageSample("s-1", 1000, 1000));
        Assert.Equal(3.00m, ledger.SpendFor("s-1"));
        Assert.Equal(3.00m, ledger.TotalSpend);
        Assert.True(ledger.TryReserve("s-1", 1000));

        ledger.Record(new EllaiUsageSample("s-1", 1000, 1000));
        Assert.Equal(6.00m, ledger.SpendFor("s-1"));
        Assert.False(ledger.TryReserve("s-1", 1000));

        ledger.Record(new EllaiUsageSample("s-2", 0, 0));
        Assert.False(ledger.TryReserve("s-2", 100));
        ledger.RecordAudio("s-2", 2.0m);
        Assert.Equal(1.00m, ledger.SpendFor("s-2"));
        Assert.Equal(7.00m, ledger.TotalSpend);
    }

    [Fact]
    public void UnpricedSpendNeverAccrues()
    {
        var ledger = new EllaiUsageLedger();

        ledger.Record(new EllaiUsageSample("s-1", 100000, 50000));
        ledger.RecordAudio("s-1", 60m);

        Assert.Equal(0, ledger.SpendFor("s-1"));
        Assert.Equal(0, ledger.TotalSpend);
        Assert.True(ledger.TryReserve("s-1", 1000));
    }
}
