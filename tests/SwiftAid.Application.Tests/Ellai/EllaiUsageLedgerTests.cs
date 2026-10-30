using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T20")]
public sealed class EllaiUsageLedgerTests
{
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
