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
}
