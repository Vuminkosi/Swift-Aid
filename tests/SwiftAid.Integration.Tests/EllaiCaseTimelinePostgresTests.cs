using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.AI;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Commands;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Integration.Tests;

public sealed partial class CaseCommandProcessingPostgresTests
{
    [FrameworkPostgresFact]
    public async Task EllaiCaseSpecialistUsesRealTimelineAndStopsAfterGrantRevocation()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        await SetInitialWorkflowVersionAsync(fixture);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var protection = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), protection);
        var consent = new EllaiConsentService();
        consent.Grant("case-agent-fixture", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromMinutes(5));
        var context = new EllaiToolContext("case-agent-fixture", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: fixture.CreateActor(),
            AllowedTools: new HashSet<string> { EllaiToolNames.CaseTimeline });

        async Task<EllaiCycleOutcome> RunAsync()
        {
            await using var db = fixture.CreateDbContext();
            var unit = new PostgresCaseCommandUnitOfWork(db);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit),
                new PostgresCaseAccessDescriptorStore(db, unit));
            var query = new CaseTimelineQuery(new PostgresCaseTimelineStore(db), policy, unit);
            var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, timeline: query);
            using var model = new CaseTimelineDelegationClient(fixture.CaseId);
            var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
                broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore(), enableCaseSpecialist: true);
            return await executor.RunAsync(new EllaiLoopRequest(context, "Read history",
                new EllaiRoleOverlay("ask", "help"), "cases", 512, "cycle", 1), default);
        }

        var allowed = await RunAsync();
        Assert.True(allowed.Completed);
        Assert.Equal(4, allowed.IterationsUsed);
        Assert.Equal(EllaiToolNames.CaseTimeline, Assert.Single(allowed.ObservedTools!).ToolName);
        Assert.Equal(fixture.CaseId.ToString("D"), Assert.Single(Assert.Single(allowed.Decision!.Claims).Evidence).ResourceId);
        await using (var db = fixture.CreateDbContext())
            await db.AccessGrantOperations.Where(item => item.GrantGroupId == fixture.GrantGroupId && item.Operation == CaseOperation.ReadCase)
                .ExecuteDeleteAsync();
        var denied = await RunAsync();
        Assert.False(denied.Completed);
        Assert.Null(denied.Decision);
    }

    private sealed class CaseTimelineDelegationClient(Guid caseId) : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("delegate", "ellai_delegate_case", new Dictionary<string, object?> { ["task"] = "Read case history" })])));
            if (_calls == 2)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("timeline", EllaiToolNames.CaseTimeline, new Dictionary<string, object?> { ["case_id"] = caseId.ToString("D") })])));
            var json = """
                {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Report accepted.","spokenText":"Report accepted.",
                "claims":[{"claimId":"c1","text":"Report accepted.","kind":"Observed","evidence":[{"sourceKind":"CaseQueue","resourceId":"CASE_ID","version":"2","fieldPath":null,"evidenceId":"CASE_ID"}]}],
                "questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
                """.Replace("CASE_ID", caseId.ToString("D"), StringComparison.Ordinal);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [FrameworkPostgresFact]
    public async Task EllaiTimelineUsesRealCaseAuthorizationAndDeniesRevokedRead()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        await SetInitialWorkflowVersionAsync(fixture);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var protection = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), protection);
        var consent = new EllaiConsentService();
        consent.Grant("timeline-fixture", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromMinutes(5));
        var toolContext = new EllaiToolContext("timeline-fixture", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: fixture.CreateActor(),
            AllowedTools: new HashSet<string> { EllaiToolNames.CaseTimeline });
        var request = new EllaiToolRequest(EllaiToolNames.CaseTimeline,
            new Dictionary<string, string> { ["case_id"] = fixture.CaseId.ToString("D") });

        async Task<EllaiToolResult> ReadAsync()
        {
            await using var db = fixture.CreateDbContext();
            var unit = new PostgresCaseCommandUnitOfWork(db);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit),
                new PostgresCaseAccessDescriptorStore(db, unit));
            var query = new CaseTimelineQuery(new PostgresCaseTimelineStore(db), policy, unit);
            return await new EllaiToolBroker(new EllaiPolicyService(), consent, timeline: query)
                .InvokeAsync(toolContext, request, default);
        }

        var allowed = await ReadAsync();
        Assert.True(allowed.Allowed);
        Assert.NotEmpty(allowed.ResultSummary);
        Assert.Equal(fixture.CaseId.ToString("D"), Assert.Single(allowed.Evidence).ResourceId);
        await using (var db = fixture.CreateDbContext())
            await db.AccessGrantOperations.Where(item => item.GrantGroupId == fixture.GrantGroupId && item.Operation == CaseOperation.ReadCase)
                .ExecuteDeleteAsync();
        var denied = await ReadAsync();
        Assert.False(denied.Allowed);
        Assert.Empty(denied.Evidence);
    }
}
