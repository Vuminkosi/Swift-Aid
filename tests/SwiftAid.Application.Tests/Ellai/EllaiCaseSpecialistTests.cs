using System.Security.Claims;
using Microsoft.Extensions.AI;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiCaseSpecialistTests
{
    [SpecialistDeadlineFact]
    public async Task SpecialistDeadlineCancelsProviderAndWithholdsResponse()
    {
        using var model = new HangingSpecialistClient();
        var agent = new EllaiFrameworkAgent(model);
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => agent.RunAsync(Context(), "policy", [new ChatMessage(ChatRole.User, "history")], 512,
            _ => Task.FromResult(true), (_, _, _) => throw new InvalidOperationException(), Usage(), default,
            enableCaseSpecialist: true));
        Assert.True(model.CancellationObserved);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, error.Code);
    }

    private sealed class SpecialistDeadlineFactAttribute : FactAttribute
    {
        public SpecialistDeadlineFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SWIFTAID_RUN_SPECIALIST_DEADLINE") != "1")
                Skip = "Opt in to the 90-second specialist deadline verification.";
        }
    }

    private sealed class HangingSpecialistClient : IChatClient
    {
        private int _calls;
        public bool CancellationObserved { get; private set; }
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("delegate", "ellai_delegate_case", new Dictionary<string, object?> { ["task"] = "Read case history" })]));
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
            throw new InvalidOperationException();
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task SupervisorDelegationKeepsCaseScopeAndObservedEvidence()
    {
        var caseId = Guid.NewGuid();
        using var model = new CaseClient(caseId);
        var context = Context();
        var agent = new EllaiFrameworkAgent(model);
        var calls = 0;
        var decision = await agent.RunAsync(context, "Trusted policy", [new ChatMessage(ChatRole.User, "Read history")],
            512, _ => Task.FromResult(true), (child, request, _) =>
            {
                calls++;
                Assert.Same(context.Actor, child.Actor);
                Assert.Equal(context.SessionId, child.SessionId);
                Assert.Equal(context.ScopeEpoch, child.ScopeEpoch);
                Assert.Equal(EllaiToolNames.CaseTimeline, request.ToolName);
                Assert.Equal(caseId.ToString("D"), request.Arguments["case_id"]);
                Assert.DoesNotContain(EllaiToolNames.DocsGet, child.AllowedTools!);
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null,
                    [new EvidenceRef("CaseQueue", caseId.ToString("D"), "3", null, caseId.ToString("D"))],
                    "Assignment acknowledged.", 1, false));
            }, Usage(), default, enableCaseSpecialist: true);

        Assert.NotNull(decision);
        Assert.Equal(caseId.ToString("D"), Assert.Single(Assert.Single(decision.Claims).Evidence).ResourceId);
        Assert.Equal(2, calls);
        Assert.Equal(4, agent.LastModelCallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CaseQueueDelegationRechecksExactEvidence(int failureMode)
    {
        var caseId = Guid.NewGuid();
        using var model = new CaseClient(caseId, queue: true);
        var context = Context() with { AllowedTools = new HashSet<string> { EllaiToolNames.State } };
        var calls = 0;
        var agent = new EllaiFrameworkAgent(model);
        async Task<EllaiDecision?> Run() => await agent.RunAsync(context, "Trusted policy", [new ChatMessage(ChatRole.User, "Find my cases")],
            512, _ => Task.FromResult(true), (child, request, _) =>
            {
                Assert.Equal(EllaiToolNames.State, request.ToolName);
                Assert.Equal("queue", request.Arguments["projection"]);
                Assert.Same(context.Actor, child.Actor);
                Assert.Equal(context.SessionId, child.SessionId);
                Assert.DoesNotContain(EllaiToolNames.CaseTimeline, child.AllowedTools!);
                calls++;
                if (failureMode == 2 && calls > 1) throw new InvalidOperationException("synthetic read failure");
                var version = failureMode == 1 && calls > 1 ? "4" : "3";
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null,
                    [new EvidenceRef("CaseQueue", caseId.ToString("D"), version, null, caseId.ToString("D"))],
                    "Assignment acknowledged.", 1, false));
            }, Usage(), default, enableCaseSpecialist: true);
        if (failureMode != 0)
        {
            try { Assert.Null(await Run()); }
            catch (EllaiFrameworkFailureException error) { Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, error.Code); }
        }
        else
        {
            var decision = await Run();
            Assert.NotNull(decision);
            Assert.Equal(caseId.ToString("D"), Assert.Single(Assert.Single(decision.Claims).Evidence).ResourceId);
        }
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyQueueIsRecheckedAndNeverInventsEvidence(bool becomesNonEmpty)
    {
        using var model = new CaseClient(Guid.NewGuid(), queue: true);
        var context = Context() with { AllowedTools = new HashSet<string> { EllaiToolNames.State } };
        var calls = 0;
        var agent = new EllaiFrameworkAgent(model);
        var decision = await agent.RunAsync(context, "policy", [new ChatMessage(ChatRole.User, "Find my cases")], 512,
            _ => Task.FromResult(true), (_, request, _) =>
            {
                calls++;
                if (becomesNonEmpty && calls > 1)
                    return Task.FromResult(new EllaiToolResult(request.ToolName, true, null,
                        [new EvidenceRef("CaseQueue", Guid.NewGuid().ToString("D"), "2", null, "case")], "A case was found.", 1, false));
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [],
                    "Owned cases only; review status is not supplied.\nThe permitted work queue is empty.", 0, false));
            }, Usage(), default, enableCaseSpecialist: true);
        if (becomesNonEmpty)
        {
            Assert.Null(decision);
        }
        else
        {
            Assert.NotNull(decision);
            Assert.Equal("No matching cases were found in your current owned-case queue.", decision.DisplayText);
            Assert.Empty(decision.Claims);
        }
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task CaseSpecialistRejectsMissingActorOrParentToolAuthority()
    {
        using var model = new CaseClient(Guid.NewGuid());
        var specialist = new EllaiCaseSpecialist(model);
        var context = Context();
        static Task<EllaiToolResult> Invoke(EllaiToolContext _, EllaiToolRequest __, CancellationToken ___) =>
            throw new InvalidOperationException();
        var anonymous = await specialist.RunAsync("history", "policy", true, context with { Actor = null },
            Usage(), _ => Task.FromResult(true), Invoke, default);
        var denied = await specialist.RunAsync("history", "policy", true,
            context with { AllowedTools = new HashSet<string> { EllaiToolNames.DocsGet } },
            Usage(), _ => Task.FromResult(true), Invoke, default);
        Assert.Equal(EllaiSpecialistOutcome.Denied, anonymous.Outcome);
        Assert.Equal(EllaiSpecialistOutcome.Denied, denied.Outcome);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task CaseSpecialistCannotUseNonQueueStateProjection()
    {
        using var model = new CaseClient(Guid.NewGuid(), queue: true, projection: "feature");
        var context = Context() with { AllowedTools = new HashSet<string> { EllaiToolNames.State } };
        var calls = 0;
        try
        {
            var decision = await new EllaiFrameworkAgent(model).RunAsync(context, "policy", [new ChatMessage(ChatRole.User, "cases")], 512,
                _ => Task.FromResult(true), (_, request, _) =>
                {
                    calls++;
                    return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "private", 1, false));
                }, Usage(), default, enableCaseSpecialist: true);
            Assert.Null(decision);
        }
        catch (EllaiFrameworkFailureException) { }
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SupervisorRejectsCaseDelegationWhenFeatureIsDisabled()
    {
        using var model = new CaseClient(Guid.NewGuid());
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => new EllaiFrameworkAgent(model).RunAsync(
            Context(), "policy", [new ChatMessage(ChatRole.User, "history")], 512, _ => Task.FromResult(true),
            (_, _, _) => throw new InvalidOperationException(), Usage(), default));
        Assert.Equal(EllaiToolReasonCodes.UnregisteredTool, error.Code);
    }

    [Fact]
    public async Task RevocationDuringCaseReadPreventsDelegatedAnswer()
    {
        using var model = new CaseClient(Guid.NewGuid());
        var current = true;
        var agent = new EllaiFrameworkAgent(model);
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => agent.RunAsync(Context(), "policy", [new ChatMessage(ChatRole.User, "history")], 512,
            _ => Task.FromResult(current), (_, request, _) =>
            {
                current = false;
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "private", 1, false));
            }, Usage(), default, enableCaseSpecialist: true));
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, error.Code);
    }

    private static EllaiToolContext Context()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "officer"), new Claim(ActorContext.AccessEpochClaimType, "1")], "fixture"));
        return new("session", "epoch", "cases", null, 4, true, Actor: ActorContext.FromValidatedPrincipal(principal),
            AllowedTools: new HashSet<string> { EllaiToolNames.CaseTimeline, EllaiToolNames.DocsGet });
    }

    private static EllaiFrameworkUsage Usage() => new("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore());

    private sealed class CaseClient(Guid caseId, bool queue = false, string projection = "queue") : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("delegate", "ellai_delegate_case", new Dictionary<string, object?> { ["task"] = "Read the case history" })])));
            if (Calls == 2)
            {
                if (queue)
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("queue", EllaiToolNames.State, new Dictionary<string, object?> { ["projection"] = projection })])));
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("timeline", EllaiToolNames.CaseTimeline, new Dictionary<string, object?> { ["case_id"] = caseId.ToString("D") })])));
            }
            var json = """
                {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Assignment acknowledged.","spokenText":"Assignment acknowledged.",
                "claims":[{"claimId":"c1","text":"Assignment acknowledged.","kind":"Observed","evidence":[{"sourceKind":"CaseQueue","resourceId":"CASE_ID","version":"3","fieldPath":null,"evidenceId":"CASE_ID"}]}],
                "questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
                """.Replace("CASE_ID", caseId.ToString("D"), StringComparison.Ordinal);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
