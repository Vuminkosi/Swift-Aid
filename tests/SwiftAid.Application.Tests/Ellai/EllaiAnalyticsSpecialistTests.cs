using System.Security.Claims;
using Microsoft.Extensions.AI;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiAnalyticsSpecialistTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegationRechecksExactAuthorizedAggregateBeforePublishing(bool changeScope)
    {
        using var model = new AnalyticsClient();
        var context = Context();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var calls = 0;
        var agent = new EllaiFrameworkAgent(model);
        var decision = await agent.RunAsync(context, "Trusted policy", [new ChatMessage(ChatRole.User, "How many cases in 30 days?")],
            512, _ => Task.FromResult(true), (child, request, _) =>
            {
                calls++;
                Assert.Same(context.Actor, child.Actor);
                Assert.Equal(context.SessionId, child.SessionId);
                Assert.Equal(context.ScopeEpoch, child.ScopeEpoch);
                Assert.Equal(EllaiToolNames.OwnedCaseCount, request.ToolName);
                Assert.Equal("30", request.Arguments["window_days"]);
                var scope = EllaiCountReplayScope.Create(30, changeScope && calls > 1 ? [Guid.NewGuid(), Guid.NewGuid()] : ids);
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null,
                    [new EvidenceRef("DashboardAggregate", "owned-cases", "version", null, "evidence")],
                    "2 managed case(s) in scope.", 1, false) { CountReplayScope = scope });
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            enableAnalyticsSpecialist: true);
        if (changeScope) Assert.Null(decision);
        else
        {
            Assert.NotNull(decision);
            Assert.Equal("2 managed case(s) in scope.", decision.DisplayText);
            Assert.Equal("DashboardAggregate", Assert.Single(Assert.Single(decision.Claims).Evidence).SourceKind);
            Assert.True(calls >= 3);
        }
    }

    [Fact]
    public async Task SpecialistRejectsMissingActorOrToolAuthorityWithoutModelCall()
    {
        using var model = new AnalyticsClient();
        var specialist = new EllaiAnalyticsSpecialist(model);
        var context = Context();
        static Task<EllaiToolResult> Invoke(EllaiToolContext _, EllaiToolRequest __, CancellationToken ___) => throw new InvalidOperationException();
        var usage = new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var anonymous = await specialist.RunAsync("count", "policy", true, context with { Actor = null }, usage,
            _ => Task.FromResult(true), Invoke, Invoke, default);
        var denied = await specialist.RunAsync("count", "policy", true,
            context with { AllowedTools = new HashSet<string> { EllaiToolNames.CaseTimeline } }, usage,
            _ => Task.FromResult(true), Invoke, Invoke, default);
        Assert.Equal(EllaiSpecialistOutcome.Denied, anonymous.Outcome);
        Assert.Equal(EllaiSpecialistOutcome.Denied, denied.Outcome);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task SupervisorDoesNotExposeAnalyticsDelegationWhenDisabled()
    {
        using var model = new AnalyticsClient();
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => new EllaiFrameworkAgent(model).RunAsync(
            Context(), "policy", [new ChatMessage(ChatRole.User, "count")], 512, _ => Task.FromResult(true),
            (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default));
        Assert.Equal(EllaiToolReasonCodes.UnregisteredTool, error.Code);
    }

    private static EllaiToolContext Context()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "officer"), new Claim(ActorContext.AccessEpochClaimType, "1")], "fixture"));
        return new("session", "epoch", "cases", null, 4, true, Actor: ActorContext.FromValidatedPrincipal(principal),
            AllowedTools: new HashSet<string> { EllaiToolNames.OwnedCaseCount });
    }

    private sealed class AnalyticsClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("delegate", "ellai_delegate_analytics", new Dictionary<string, object?> { ["task"] = "Count my cases from the last 30 days" })])));
            if (Calls == 2)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("count", EllaiToolNames.OwnedCaseCount, new Dictionary<string, object?> { ["window_days"] = "30" })])));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
