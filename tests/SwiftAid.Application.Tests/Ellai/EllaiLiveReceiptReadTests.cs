using SwiftAid.Application.Ellai;
using SwiftAid.Intelligence.Meta;
using Microsoft.Extensions.AI;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T07")]
public sealed class EllaiLiveReceiptReadTests
{
    private static readonly EllaiToolContext Context = new(
        "session-1", "epoch-1", "cases", "case-1", 4, true, "consent/1", EllaiProcessingClassification.SyntheticDemo);

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private static EllaiToolBroker GrantedBroker(IEllaiApprovalStore? receipts = null)
    {
        var consent = new EllaiConsentService();
        consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        return new EllaiToolBroker(new EllaiPolicyService(), consent, null, null, receipts);
    }

    private static IReadOnlyDictionary<string, string> Args(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    private static async Task<EllaiReceipt> RecordAsync(
        EllaiReceiptStore store, string session, string action, string key, DateTimeOffset now) =>
        await store.RecordAsync(session, action, key, "hash-1", 1,
            "officer-1", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
            now, Lifetime, "policy-hash", CancellationToken.None);

    [Fact]
    public async Task ReceiptLookupReturnsLiveStatusAndBinding()
    {
        var store = new EllaiReceiptStore();
        var now = DateTimeOffset.UtcNow;
        var recorded = await RecordAsync(store, "session-1", "action-1", "key-1", now);
        var broker = GrantedBroker(store);

        var result = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.Receipts, Args(("receipt", recorded.ReceiptId))),
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal(1, result.ItemCount);
        Assert.Contains("Accepted", result.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("officer-1", result.ResultSummary, StringComparison.Ordinal);
        Assert.Equal("Accepted", result.Evidence.Single().Version);
        Assert.True(EllaiToolBroker.MeasureEnvelopeBytes(result) <= EllaiToolBroker.MaxResultBytes);
    }

    [Fact]
    public async Task UnknownReceiptIsAnAuthorizedEmptyNotADenial()
    {
        var broker = GrantedBroker(new EllaiReceiptStore());

        var result = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.Receipts, Args(("receipt", "rcpt-missing"))),
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal(0, result.ItemCount);
        Assert.Contains("No matching receipt", result.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossSessionReceiptReadsAsAbsent()
    {
        var store = new EllaiReceiptStore();
        var recorded = await RecordAsync(store, "session-2", "action-1", "key-1", DateTimeOffset.UtcNow);
        var broker = GrantedBroker(store);

        var lookup = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.Receipts, Args(("receipt", recorded.ReceiptId))),
            CancellationToken.None);
        Assert.True(lookup.Allowed);
        Assert.Equal(0, lookup.ItemCount);
        Assert.DoesNotContain(recorded.ReceiptId, lookup.ResultSummary, StringComparison.Ordinal);

        var list = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.Receipts, Args()),
            CancellationToken.None);
        Assert.True(list.Allowed);
        Assert.Equal(0, list.ItemCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public async Task MisScopedReceiptListFailsClosedBeforeReturningAnyRows(int foreignIndex)
    {
        var now = DateTimeOffset.UtcNow;
        var receipts = Enumerable.Range(0, 21).Select(index => new EllaiReceipt(
            $"receipt-{index}", index == foreignIndex ? "session-2" : "session-1", $"action-{index}",
            $"key-{index}", EllaiReceiptStatus.Accepted, now, now.AddMinutes(5), null)).ToArray();
        var result = await GrantedBroker(new MisScopedReceiptStore(receipts)).InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.Receipts, Args()), CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, result.ReasonCode);
        Assert.Empty(result.Evidence);
        Assert.Equal(0, result.ItemCount);
        Assert.DoesNotContain("receipt-", result.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FrameworkToolDoesNotExposeMisScopedReceiptToModel()
    {
        var now = DateTimeOffset.UtcNow;
        var foreign = new EllaiReceipt("foreign-receipt", "session-2", "foreign-action", "foreign-key",
            EllaiReceiptStatus.Accepted, now, now.AddMinutes(5), null, ApprovedRecipient: "foreign-recipient");
        var broker = GrantedBroker(new MisScopedReceiptStore([foreign]));
        var tool = new EllaiFrameworkTool(EllaiToolNames.Receipts,
            (request, ct) => broker.InvokeAsync(Context, request, ct));

        var serialized = Assert.IsType<string>(await tool.InvokeAsync(new AIFunctionArguments()));
        Assert.DoesNotContain("foreign-", serialized, StringComparison.Ordinal);
        using var response = System.Text.Json.JsonDocument.Parse(serialized);
        Assert.False(response.RootElement.GetProperty("allowed").GetBoolean());
        Assert.Equal(EllaiToolReasonCodes.Unavailable, response.RootElement.GetProperty("reasonCode").GetString());
    }

    private sealed class MisScopedReceiptStore(IReadOnlyList<EllaiReceipt> receipts) : IEllaiApprovalStore
    {
        public Task<IReadOnlyList<EllaiReceipt>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(receipts);
        public Task<EllaiReceipt?> GetAsync(string receiptId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EllaiReceipt> RecordAsync(string sessionId, string actionId, string idempotencyKey,
            string planHash, int planVersion, string? approvedRecipient, DateTimeOffset? approvedDueAtUtc,
            DateTimeOffset now, TimeSpan approvalLifetime, string policyHash, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<EllaiReceipt?> MarkStatusAsync(string receiptId, EllaiReceiptStatus status, DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task SessionListShowsVerifiedOutcomeHonestly()
    {
        var store = new EllaiReceiptStore();
        var now = DateTimeOffset.UtcNow;
        var recorded = await RecordAsync(store, "session-1", "action-1", "key-1", now);
        await store.MarkStatusAsync(recorded.ReceiptId, EllaiReceiptStatus.Applied,
            now.AddMinutes(16), CancellationToken.None);
        var broker = GrantedBroker(store);

        var list = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.Receipts, Args()),
            CancellationToken.None);

        Assert.True(list.Allowed);
        Assert.Equal(1, list.ItemCount);
        Assert.Contains("UnknownOutcome", list.ResultSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("Applied", list.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingReceiptAuthorityYieldsUnavailable()
    {
        var result = await GrantedBroker().InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.Receipts, Args()),
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, result.ReasonCode);
    }
}
