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
    public async Task EmptyCaseQueueIsRecheckedAndInvalidatedWhenAReadableCaseAppears()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        await db.ManagedCases.Where(row => row.CaseId == fixture.CaseId || row.CaseId == fixture.SiblingCaseId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.AccountableOwnerActorId, fixture.NewOwnerActorId));
        var unit = new PostgresCaseCommandUnitOfWork(db);
        var queue = new CaseQueueQuery(new PostgresCaseQueueStore(db),
            new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit)), unit);
        var consent = new EllaiConsentService();
        consent.Grant("empty-case-agent", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromMinutes(5));
        var context = new EllaiToolContext("empty-case-agent", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: fixture.CreateActor(),
            AllowedTools: new HashSet<string> { EllaiToolNames.State });
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: queue);
        async Task<EllaiCycleOutcome> RunAsync(bool addCase)
        {
            using var model = new CaseQueueDelegationClient(fixture.CaseId);
            var calls = 0;
            var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
                broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore(), enableCaseSpecialist: true);
            return await executor.RunAsync(new EllaiLoopRequest(context, "Find my cases in Eswatini",
                new EllaiRoleOverlay("ask", "Be brief."), "queue", 1024, "cycle", 1), default,
                invokeToolAsync: async (child, request, ct) =>
                {
                    var result = await broker.InvokeAsync(child, request, ct);
                    if (++calls == 1 && addCase)
                    {
                        await using var updateDb = fixture.CreateDbContext();
                        await updateDb.ManagedCases.Where(row => row.CaseId == fixture.CaseId)
                            .ExecuteUpdateAsync(update => update.SetProperty(row => row.AccountableOwnerActorId, fixture.ActorId), ct);
                    }
                    return result;
                });
        }
        var empty = await RunAsync(false);
        Assert.True(empty.Completed, empty.FailureCode);
        Assert.Equal("No matching cases were found in your current owned-case queue.", empty.Decision!.DisplayText);
        Assert.Empty(empty.Decision.Claims);
        Assert.Empty(Assert.Single(empty.ObservedTools!).Evidence);
        var changed = await RunAsync(true);
        Assert.False(changed.Completed);
        Assert.Null(changed.Decision);
        var trace = EllaiToolTrace.SerializeFlattened(empty.ObservedTools, false, []);
        var previous = new EllaiConversationTurn(context.SessionId, "previous", 1, "Find cases", empty.Decision.DisplayText,
            "Completed", 1, DateTimeOffset.UtcNow) { ToolTraceJson = trace };
        var current = new EllaiConversationTurn(context.SessionId, "current", 2, "Continue", null, "Queued", 1, null);
        var history = await EllaiAuthorizedConversationContext.BuildAsync([previous, current], "current", fixture.CreateActor(), queue, default);
        Assert.DoesNotContain(history.Messages, item => item.Content == empty.Decision.DisplayText);
    }

    [FrameworkPostgresFact]
    public async Task CaseSpecialistSearchesAuthorizedPostgresQueueAndRechecksBeforeAnswer()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        var unit = new PostgresCaseCommandUnitOfWork(db);
        var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
        var queue = new CaseQueueQuery(new PostgresCaseQueueStore(db), policy, unit);
        var consent = new EllaiConsentService();
        consent.Grant("case-queue-agent", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromMinutes(5));
        var context = new EllaiToolContext("case-queue-agent", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: fixture.CreateActor(),
            AllowedTools: new HashSet<string> { EllaiToolNames.State });
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: queue);
        async Task<EllaiCycleOutcome> RunAsync(bool revoke)
        {
            using var model = new CaseQueueDelegationClient(fixture.CaseId);
            var calls = 0;
            var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
                broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore(), enableCaseSpecialist: true);
            return await executor.RunAsync(new EllaiLoopRequest(context, "Find my cases in Eswatini",
                new EllaiRoleOverlay("ask", "Be brief."), "queue", 1024, "cycle", 1), default,
                invokeToolAsync: async (child, request, ct) =>
                {
                    var result = await broker.InvokeAsync(child, request, ct);
                    if (++calls == 1 && revoke) await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
                    return result;
                });
        }
        var allowed = await RunAsync(false);
        Assert.True(allowed.Completed, allowed.FailureCode);
        Assert.Equal(EllaiToolNames.State, Assert.Single(allowed.ObservedTools!).ToolName);
        Assert.Contains(allowed.Decision!.Claims.SelectMany(claim => claim.Evidence), evidence => evidence.ResourceId == fixture.CaseId.ToString("D"));
        var denied = await RunAsync(true);
        Assert.False(denied.Completed);
        Assert.Null(denied.Decision);
    }

    private sealed class CaseQueueDelegationClient(Guid caseId) : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (++_calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("delegate", "ellai_delegate_case", new Dictionary<string, object?> { ["task"] = "Find my cases in Eswatini" })])));
            if (_calls == 2)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("queue", EllaiToolNames.State, new Dictionary<string, object?> { ["projection"] = "queue", ["country"] = "SZ" })])));
            var json = """
                {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"A case is recorded in Eswatini.","spokenText":"A case is recorded in Eswatini.",
                "claims":[{"claimId":"c1","text":"A case is recorded in Eswatini.","kind":"Observed","evidence":[{"sourceKind":"CaseQueue","resourceId":"CASE_ID","version":"2","fieldPath":null,"evidenceId":"CASE_ID"}]}],
                "questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
                """.Replace("CASE_ID", caseId.ToString("D"), StringComparison.Ordinal);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [FrameworkPostgresLiveMuseFact]
    public async Task LiveMuseDelegatesCaseHistoryThroughAuthorizedPostgresTimeline()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        await SetInitialWorkflowVersionAsync(fixture);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var protection = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), protection);
        await using var db = fixture.CreateDbContext();
        var unit = new PostgresCaseCommandUnitOfWork(db);
        var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit),
            new PostgresCaseAccessDescriptorStore(db, unit));
        var query = new CaseTimelineQuery(new PostgresCaseTimelineStore(db), policy, unit);
        var consent = new EllaiConsentService();
        consent.Grant("live-case-agent", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromMinutes(5));
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, timeline: query);
        var context = new EllaiToolContext("live-case-agent", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: fixture.CreateActor(),
            AllowedTools: new HashSet<string> { EllaiToolNames.CaseTimeline });
        var options = EllaiOptions.Bind(Environment.GetEnvironmentVariable);
        if (!options.HasReasoningRoute) throw new InvalidOperationException("Live Muse route is not configured.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var provider = EllaiMuseChatClientFactory.Create(options, http);
        using var observed = new CaseLiveProbeClient(provider);
        var instructions = "If ellai_delegate_case is available, call it for the question. Otherwise call " +
            $"ellai_case_timeline with case_id {fixture.CaseId:D}. Cite the exact returned evidence in an Observed claim. " +
            "Return only JSON containing final_decision with schemaVersion 2, kind Answer, displayText, spokenText, " +
            "claims, questions, uiIntents, planProposal, topicRefs, resumeSuggestion and stopReason. " +
            "Use empty arrays and null for unused fields.";
        var agent = new EllaiFrameworkAgent(observed);
        var result = await agent.RunAsync(context, instructions,
            [new ChatMessage(ChatRole.User, $"Read the history for case {fixture.CaseId:D}.")], 2048,
            _ => Task.FromResult(true), broker.InvokeAsync,
            new EllaiFrameworkUsage(context.SessionId, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            enableCaseSpecialist: true);
        Assert.NotNull(result);
        Assert.Contains("ellai_delegate_case", observed.ToolCalls);
        Assert.Contains(EllaiToolNames.CaseTimeline, observed.ToolCalls);
        Assert.Contains(result!.Claims.SelectMany(claim => claim.Evidence), evidence =>
            evidence.SourceKind == "CaseQueue" && evidence.ResourceId == fixture.CaseId.ToString("D"));
    }

    private sealed class CaseLiveProbeClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public List<string> ToolCalls { get; } = [];
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            ToolCalls.AddRange(response.Messages.SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>().Select(call => call.Name));
            return response;
        }
    }

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
