using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T03")]
public sealed class EllaiConsentPlumbingTests
{
    private static readonly EllaiToolContext Context = new(
        "session-1", "epoch-1", "cases", "case-1", 4, true);

    [Fact]
    public async Task SessionViewReportsRealGrantVersion()
    {
        var consent = new EllaiConsentService();
        var sessions = new EllaiSessionService(new InMemoryEllaiSessionStore(), consent);
        var start = await sessions.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "c-1", "cases", null, "text", "text"),
            CancellationToken.None);

        Assert.Equal("consent/0", start.ConsentVersion);

        consent.Grant(start.SessionId, EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, "synthetic", TimeSpan.FromHours(1));
        var after = await sessions.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", start.SessionId, start.SessionVersion,
                "What is overdue?", null, "typed", 1),
            CancellationToken.None);

        Assert.NotNull(after);
        Assert.Equal("consent/1", after.ConsentVersion);
    }

    [Fact]
    public async Task BrokerDeniesReadsWithoutLiveGrantWhenConsentWired()
    {
        var consent = new EllaiConsentService();
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent);

        var denied = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>()),
            CancellationToken.None);
        Assert.False(denied.Allowed);
        Assert.Contains("consent", denied.DenialReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, "synthetic", TimeSpan.FromHours(1));
        var allowed = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>()),
            CancellationToken.None);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public async Task BrokerIgnoresGrantsForOtherPurposes()
    {
        var consent = new EllaiConsentService();
        consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
            "other-purpose", "synthetic", TimeSpan.FromHours(1));
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent);

        var result = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>()),
            CancellationToken.None);
        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task RunnerStopsWhenReauthorizationFails()
    {
        var runner = new EllaiCycleRunner(new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()));
        var turns = 0;

        var outcome = await runner.RunAsync(
            Context,
            iteration =>
            {
                turns++;
                return Task.FromResult(new EllaiModelTurn(
                    Array.Empty<EllaiToolRequest>(), null));
            },
            _ => Array.Empty<string>(),
            CancellationToken.None,
            (iteration, _) => Task.FromResult(iteration < 2));

        Assert.False(outcome.Completed);
        Assert.Equal(1, turns);
        Assert.Contains("eauthorization", outcome.StopReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
