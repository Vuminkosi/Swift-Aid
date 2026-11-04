using SwiftAid.Application.Ellai;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests;

public sealed class ReportVoiceExtractionAdmissionTests
{
    [Fact]
    public async Task RequiresOwnedActiveSessionAndSeparateReportConsent()
    {
        var fixture = await Fixture.CreateAsync();
        Assert.Equal("Unavailable", (await fixture.Admission.ExtractAsync("session", "other", "A fever", default)).Status);
        Assert.Equal("ConsentRequired", (await fixture.Admission.ExtractAsync("session", "owner", "A fever", default)).Status);
        Assert.Equal(0, fixture.Client.Calls);

        await fixture.GrantAsync();
        var result = await fixture.Admission.ExtractAsync("session", "owner", "A fever", default);

        Assert.Equal("Ready", result.Status);
        Assert.Equal("fever", Assert.Single(result.Fields!).Value);
        Assert.Equal(1, fixture.Client.Calls);
        var usage = await fixture.Usage.TotalsAsync("session", default);
        Assert.Equal(15, usage.TotalTokens);
    }

    [Fact]
    public async Task RevocationDuringInferenceDiscardsTheResultAndStillAccountsUsage()
    {
        var fixture = await Fixture.CreateAsync();
        var grant = await fixture.GrantAsync();
        fixture.Client.BeforeReturn = () => fixture.Consent.Revoke(grant.GrantId);

        var result = await fixture.Admission.ExtractAsync("session", "owner", "A fever", default);

        Assert.Equal("Stale", result.Status);
        Assert.Null(result.Fields);
        Assert.Equal(15, (await fixture.Usage.TotalsAsync("session", default)).TotalTokens);
    }

    [Fact]
    public async Task ExhaustedBudgetDoesNotCallModel()
    {
        var fixture = await Fixture.CreateAsync(tokenBudget: 100);
        await fixture.GrantAsync();

        var result = await fixture.Admission.ExtractAsync("session", "owner", "A fever", default);

        Assert.Equal("BudgetUnavailable", result.Status);
        Assert.Equal(0, fixture.Client.Calls);
    }

    [Fact]
    public async Task FailedProviderCallRecordsAnEstimateRatherThanAssumingItWasFree()
    {
        var fixture = await Fixture.CreateAsync();
        await fixture.GrantAsync();
        fixture.Client.BeforeReturn = () => throw new InvalidOperationException("provider failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Admission.ExtractAsync("session", "owner", "A fever", default));

        var usage = await fixture.Usage.TotalsAsync("session", default);
        Assert.Equal(0, usage.PromptTokens);
        Assert.Equal(0, usage.CompletionTokens);
        Assert.True(usage.TotalTokens >= ReportVoiceExtraction.MaximumCompletionTokens);
    }

    [Fact]
    public async Task SessionClosureDuringInferenceDiscardsResult()
    {
        var fixture = await Fixture.CreateAsync();
        await fixture.GrantAsync();
        fixture.Client.BeforeReturn = () =>
        {
            var session = fixture.Sessions.GetAsync("session", default).GetAwaiter().GetResult()!;
            fixture.Sessions.TryUpdateAsync(session with { Status = "Cancelled", SessionVersion = 2 },
                session.SessionVersion, default).GetAwaiter().GetResult();
        };

        var result = await fixture.Admission.ExtractAsync("session", "owner", "A fever", default);

        Assert.Equal("Stale", result.Status);
        Assert.Null(result.Fields);
    }

    private sealed class Fixture
    {
        public InMemoryEllaiSessionStore Sessions { get; } = new();
        public EllaiConsentService Consent { get; } = new();
        public InMemoryEllaiConsentStore Consents { get; } = new();
        public InMemoryEllaiUsageStore Usage { get; } = new();
        public CompletionClient Client { get; } = new();
        public ReportVoiceExtractionAdmission Admission { get; private set; } = default!;

        public static async Task<Fixture> CreateAsync(int tokenBudget = 20000)
        {
            var fixture = new Fixture();
            await fixture.Sessions.CreateAsync(new EllaiSessionRecord("session", default(EllaiMode), "Active",
                1, 0, 0, 0, 0, 10, "owner", "scope", null, null, DateTimeOffset.UtcNow), default);
            fixture.Admission = new(fixture.Sessions, fixture.Consent, fixture.Consents,
                new EllaiUsageLedger(sessionTokenBudget: tokenBudget), fixture.Usage,
                new ReportVoiceExtraction(fixture.Client));
            return fixture;
        }

        public async Task<EllaiConsentGrant> GrantAsync()
        {
            var grant = Consent.Grant("session", EllaiConsentDimension.ReasoningText,
                ReportVoiceExtractionAdmission.ConsentPurpose, ReportVoiceExtractionAdmission.ConsentDataClass,
                TimeSpan.FromHours(1));
            await Consents.RecordAsync(grant, default);
            return grant;
        }
    }

    private sealed class CompletionClient : IEllaiTurnCompletionClient
    {
        public int Calls { get; private set; }
        public Action? BeforeReturn { get; set; }

        public Task<EllaiTurnCompletion> CompleteAsync(IReadOnlyList<EllaiChatMessage> messages,
            int maxTokens, CancellationToken ct)
        {
            Calls++;
            BeforeReturn?.Invoke();
            return Task.FromResult(new EllaiTurnCompletion(
                """{"fields":[{"field":"observation","value":"fever","evidence":"A fever"}]}""", 10, 5));
        }
    }
}
