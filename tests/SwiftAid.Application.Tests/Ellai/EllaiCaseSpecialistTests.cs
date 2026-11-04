using System.Security.Claims;
using Microsoft.Extensions.AI;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiCaseSpecialistTests
{
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
        Assert.Equal(1, calls);
        Assert.Equal(4, agent.LastModelCallCount);
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

    private sealed class CaseClient(Guid caseId) : IChatClient
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
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("timeline", EllaiToolNames.CaseTimeline, new Dictionary<string, object?> { ["case_id"] = caseId.ToString("D") })])));
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
