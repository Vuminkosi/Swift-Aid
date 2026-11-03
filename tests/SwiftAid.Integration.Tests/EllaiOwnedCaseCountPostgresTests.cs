using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiOwnedCaseCountPostgresTests
{
    [FrameworkPostgresFact]
    public async Task FrameworkStopsCountPublicationWhenPostgresGrantExpiresAfterFirstChunk()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        var unit = new PostgresCaseCommandUnitOfWork(db);
        var query = new DashboardAggregateQuery(new PostgresDashboardAggregateStore(db),
            new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit)),
            unit, TimeProvider.System);
        var context = new EllaiToolContext("count-publication-fixture", "scope", "cases", null, 4, true,
            "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive, fixture.CreateActor(),
            new HashSet<string> { EllaiToolNames.OwnedCaseCount }, EllaiConsentPurposes.PublicProductHelp);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), dashboard: query);
        using var model = new CountClient();
        var agent = new EllaiFrameworkAgent(model);
        var observations = new List<EllaiToolResult>();
        var chunks = new List<string>();
        var decision = await agent.RunAsync(context, "Synthetic policy", [new ChatMessage(ChatRole.User, "Count my cases")], 200,
            _ => Task.FromResult(true), async (toolContext, request, ct) =>
            {
                var result = await broker.InvokeAsync(toolContext, request, ct);
                observations.Add(result);
                return result;
            }, new EllaiFrameworkUsage(context.SessionId, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            onTextDelta: async (text, _) =>
            {
                chunks.Add(text);
                if (chunks.Count == 1) await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
            });

        Assert.Single(chunks);
        Assert.Equal(3, observations.Count);
        Assert.StartsWith("2 managed case(s)", chunks[0]);
        Assert.StartsWith("2 managed case(s)", observations[1].ResultSummary);
        Assert.StartsWith("0 managed case(s)", observations[2].ResultSummary);
        Assert.Null(decision);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, agent.LastFailureCode);
    }

    [FrameworkPostgresFact]
    public async Task FrameworkWithholdsUnchangedCountWhenPostgresMembershipChanges()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        await db.ManagedCases.Where(row => row.CaseId == fixture.SiblingCaseId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AccountableOwnerActorId, fixture.NewOwnerActorId));
        var unit = new PostgresCaseCommandUnitOfWork(db);
        var query = new DashboardAggregateQuery(new PostgresDashboardAggregateStore(db),
            new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit)),
            unit, TimeProvider.System);
        var context = new EllaiToolContext("count-membership-fixture", "scope", "cases", null, 4, true,
            "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive, fixture.CreateActor(),
            new HashSet<string> { EllaiToolNames.OwnedCaseCount }, EllaiConsentPurposes.PublicProductHelp);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), dashboard: query);
        using var model = new CountClient();
        var agent = new EllaiFrameworkAgent(model);
        var observed = new List<EllaiToolResult>();
        EllaiConversationTurn[]? priorTurns = null;
        var decision = await agent.RunAsync(context, "Synthetic policy", [new ChatMessage(ChatRole.User, "Count my cases")], 200,
            _ => Task.FromResult(true), async (toolContext, request, ct) =>
            {
                var result = await broker.InvokeAsync(toolContext, request, ct);
                observed.Add(result);
                if (observed.Count == 1)
                {
                    var trace = EllaiToolTrace.SerializeFlattened([EllaiObservedToolResult.From(request, result)], false, []);
                    priorTurns = [new(context.SessionId, "prior", 1, "Count", result.ResultSummary, "Completed", 1,
                        DateTimeOffset.UtcNow) { ToolTraceJson = trace },
                        new(context.SessionId, "current", 2, "Repeat", null, "Queued", 1, null)];
                    var replay = await EllaiAuthorizedConversationContext.BuildAsync(priorTurns, "current", context.Actor!,
                        null, ct, dashboard: query);
                    Assert.Contains(replay.Messages, item => item.Content == result.ResultSummary);
                    await using var updateDb = fixture.CreateDbContext();
                    await updateDb.ManagedCases.Where(row => row.CaseId == fixture.CaseId)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AccountableOwnerActorId, fixture.NewOwnerActorId), ct);
                    await updateDb.ManagedCases.Where(row => row.CaseId == fixture.SiblingCaseId)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AccountableOwnerActorId, fixture.ActorId), ct);
                }
                return result;
            }, new EllaiFrameworkUsage(context.SessionId, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default);

        Assert.Equal(2, observed.Count);
        Assert.All(observed, item => Assert.True(item.Allowed));
        Assert.All(observed, item => Assert.StartsWith("1 managed case(s)", item.ResultSummary));
        Assert.NotEqual(observed[0].CountReplayScope, observed[1].CountReplayScope);
        Assert.Null(decision);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, agent.LastFailureCode);
        var afterChange = await EllaiAuthorizedConversationContext.BuildAsync(priorTurns!, "current", context.Actor!,
            null, default, dashboard: query);
        Assert.DoesNotContain(afterChange.Messages, item => item.Content == observed[0].ResultSummary);
    }

    [FrameworkPostgresFact]
    public async Task FrameworkWithholdsCountWhenGrantExpiresBeforePublication()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        var unit = new PostgresCaseCommandUnitOfWork(db);
        var query = new DashboardAggregateQuery(new PostgresDashboardAggregateStore(db),
            new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit)),
            unit, TimeProvider.System);
        var context = new EllaiToolContext("count-fixture", "scope", "cases", null, 4, true,
            "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive, fixture.CreateActor(),
            new HashSet<string> { EllaiToolNames.OwnedCaseCount }, EllaiConsentPurposes.PublicProductHelp);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), dashboard: query);
        using var model = new CountClient();
        var agent = new EllaiFrameworkAgent(model);
        var calls = 0;
        var decision = await agent.RunAsync(context, "Synthetic policy", [new ChatMessage(ChatRole.User, "Count my cases")], 200,
            _ => Task.FromResult(true), async (toolContext, request, ct) =>
            {
                var result = await broker.InvokeAsync(toolContext, request, ct);
                if (++calls == 1) await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
                return result;
            }, new EllaiFrameworkUsage(context.SessionId, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default);
        Assert.Null(decision);
        Assert.Equal(2, calls);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, agent.LastFailureCode);
    }

    private sealed class LiveCountFactAttribute : FactAttribute
    {
        public LiveCountFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("ELLAI_LIVE") != "1" ||
                Environment.GetEnvironmentVariable("SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES") != "1")
                Skip = "Requires the live Muse route and dedicated disposable PostgreSQL fixture.";
        }
    }

    [LiveCountFact]
    public async Task LiveMuseExecutorUsesAuthorizedCountForOrdinaryQuestion()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        var unit = new PostgresCaseCommandUnitOfWork(db);
        var query = new DashboardAggregateQuery(new PostgresDashboardAggregateStore(db),
            new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit)),
            unit, TimeProvider.System);
        var actor = fixture.CreateActor();
        var expected = await query.GetAsync(actor, TimeSpan.FromDays(30), default);
        Assert.Equal(DashboardAggregateAccess.Allowed, expected.Access);
        Assert.False(expected.Truncated);
        Assert.NotEmpty(expected.Rows);
        var context = new EllaiToolContext("live-executor-count-fixture", "scope", "cases", null, 4, true,
            "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive, actor,
            new HashSet<string> { EllaiToolNames.OwnedCaseCount }, EllaiConsentPurposes.PublicProductHelp);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), dashboard: query);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = EllaiMuseChatClientFactory.Create(EllaiOptions.Bind(Environment.GetEnvironmentVariable), http);
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Synthetic test policy", "test"),
            broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var calls = 0;
        var result = await executor.RunAsync(new EllaiLoopRequest(context,
            "How many of my readable managed cases were created in the last 30 days?",
            new EllaiRoleOverlay("ask", "Be brief."), "count", 1024, "cycle", 1), default,
            invokeToolAsync: async (toolContext, request, ct) =>
            {
                calls++;
                return await broker.InvokeAsync(toolContext, request, ct);
            });
        Assert.True(result.Completed, $"{result.FailureCode}; model calls={result.IterationsUsed}; tool invocations={calls}; repair={result.RepairUsed}");
        Assert.Equal(2, calls); // The authorized count is rechecked before publication.
        Assert.Contains($"{expected.Rows.Count} managed case(s)", result.Decision!.DisplayText);
        Assert.Contains(result.Decision.Claims, claim => claim.Evidence.Any(item => item.SourceKind == "DashboardAggregate"));

        var trace = EllaiToolTrace.SerializeFlattened(result.ObservedTools, false, []);
        Assert.NotNull(trace);
        var turns = new[]
        {
            new EllaiConversationTurn(context.SessionId, "count-turn", 1, "How many of my readable managed cases were created in the last 30 days?",
                result.Decision.DisplayText, "Completed", 1, DateTimeOffset.UtcNow) { ToolTraceJson = trace },
            new EllaiConversationTurn(context.SessionId, "followup", 2, "Update that same count now using the same date window as before.",
                null, "Queued", 1, null)
        };
        var authorized = await EllaiAuthorizedConversationContext.BuildAsync(turns, "followup", actor, null, default,
            dashboard: query, toolContext: context);
        Assert.Contains(authorized.Messages, message => message.Content == result.Decision.DisplayText);
        var followupCalls = new List<EllaiToolRequest>();
        var followup = await executor.RunAsync(new EllaiLoopRequest(context,
            "Update that same count now using the same date window as before.",
            new EllaiRoleOverlay("ask", "Be brief."), "count", 2048, "followup-cycle", 1,
            authorized.Messages), default, invokeToolAsync: async (toolContext, request, ct) =>
            {
                followupCalls.Add(request);
                return await broker.InvokeAsync(toolContext, request, ct);
            });
        Assert.True(followup.Completed, followup.FailureCode);
        Assert.Equal(2, followupCalls.Count);
        Assert.All(followupCalls, call => Assert.Equal("30", call.Arguments["window_days"]));
        Assert.Matches($@"(?<!\d){expected.Rows.Count}(?!\d)", followup.Decision!.DisplayText);

        await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var revokedDb = fixture.CreateDbContext();
        var revokedUnit = new PostgresCaseCommandUnitOfWork(revokedDb);
        var revokedQuery = new DashboardAggregateQuery(new PostgresDashboardAggregateStore(revokedDb),
            new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(revokedDb, revokedUnit),
                new PostgresCaseAccessDescriptorStore(revokedDb, revokedUnit)), revokedUnit, TimeProvider.System);
        var revoked = await EllaiAuthorizedConversationContext.BuildAsync(turns, "followup", actor, null, default,
            dashboard: revokedQuery, toolContext: context);
        Assert.DoesNotContain(revoked.Messages, message => message.Content == result.Decision.DisplayText);
    }

    [LiveCountFact]
    public async Task LiveMuseUsesAuthorizedPostgresCountAndCitesObservedEvidence()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        var unit = new PostgresCaseCommandUnitOfWork(db);
        var query = new DashboardAggregateQuery(new PostgresDashboardAggregateStore(db),
            new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit)),
            unit, TimeProvider.System);
        var actor = fixture.CreateActor();
        var expected = await query.GetAsync(actor, TimeSpan.FromDays(30), default);
        Assert.Equal(DashboardAggregateAccess.Allowed, expected.Access);
        Assert.False(expected.Truncated);
        Assert.NotEmpty(expected.Rows);
        var context = new EllaiToolContext("live-count-fixture", "scope", "cases", null, 4, true,
            "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive, actor,
            new HashSet<string> { EllaiToolNames.OwnedCaseCount }, EllaiConsentPurposes.PublicProductHelp);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), dashboard: query);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = EllaiMuseChatClientFactory.Create(EllaiOptions.Bind(Environment.GetEnvironmentVariable), http);
        EllaiToolResult? observed = null;
        var agent = new EllaiFrameworkAgent(client);
        const string instructions = "Use the available count tool, then return only JSON: " +
            "{\"final_decision\":{\"schemaVersion\":2,\"kind\":\"Answer\",\"displayText\":\"...\",\"spokenText\":\"...\"," +
            "\"claims\":[{\"claimId\":\"c1\",\"text\":\"...\",\"kind\":\"Observed\",\"evidence\":[<exact evidence object from tool>]}]," +
            "\"questions\":[],\"uiIntents\":[],\"planProposal\":null,\"topicRefs\":[],\"resumeSuggestion\":null,\"stopReason\":null}}.";
        var decision = await agent.RunAsync(context, instructions,
            [new ChatMessage(ChatRole.User, "Use ellai_owned_case_count with window_days 30. State the count and cite the exact returned evidence.")],
            1024, _ => Task.FromResult(true), async (toolContext, request, ct) =>
            {
                observed = await broker.InvokeAsync(toolContext, request, ct);
                return observed;
            }, new EllaiFrameworkUsage(context.SessionId, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default);
        Assert.NotNull(observed);
        Assert.True(observed.Allowed);
        Assert.Contains($"{expected.Rows.Count} managed case(s)", observed.ResultSummary);
        Assert.NotNull(decision);
        var number = expected.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var exactNumber = $@"(?<!\d){number}(?!\d)";
        Assert.Matches(exactNumber, decision.DisplayText);
        Assert.Contains(decision.Claims, claim => System.Text.RegularExpressions.Regex.IsMatch(claim.Text, exactNumber) &&
            claim.Evidence.Contains(Assert.Single(observed.Evidence)));
    }

    [FrameworkPostgresFact]
    public async Task FrameworkCountUsesCurrentPostgresAuthorizationAndOmitsRevokedCases()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        var actor = fixture.CreateActor();
        var context = new EllaiToolContext("count-fixture", "scope", "cases", null, 4, true,
            "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive, actor,
            new HashSet<string> { EllaiToolNames.OwnedCaseCount }, EllaiConsentPurposes.PublicProductHelp);

        async Task<int> RunAsync()
        {
            await using var db = fixture.CreateDbContext();
            var unit = new PostgresCaseCommandUnitOfWork(db);
            var query = new DashboardAggregateQuery(new PostgresDashboardAggregateStore(db),
                new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit)),
                unit, TimeProvider.System);
            var expected = await query.GetAsync(actor, TimeSpan.FromDays(30), default);
            Assert.Equal(DashboardAggregateAccess.Allowed, expected.Access);
            Assert.False(expected.Truncated);
            var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), dashboard: query);
            using var model = new CountClient();
            var agent = new EllaiFrameworkAgent(model);
            var observations = new List<EllaiToolResult>();
            var decision = await agent.RunAsync(context, "Synthetic policy", [new ChatMessage(ChatRole.User, "Count my cases")], 200,
                _ => Task.FromResult(true), async (toolContext, request, ct) =>
                {
                    var result = await broker.InvokeAsync(toolContext, request, ct);
                    observations.Add(result);
                    return result;
                },
                new EllaiFrameworkUsage(context.SessionId, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default);
            Assert.NotNull(decision);
            Assert.Equal(2, observations.Count);
            Assert.All(observations, observation => Assert.True(observation.Allowed));
            Assert.Contains($"{expected.Rows.Count} managed case(s)", observations[0].ResultSummary);
            Assert.Contains($"{expected.Rows.Count} managed case(s)", decision.DisplayText);
            Assert.DoesNotContain(fixture.CaseId.ToString("D"), System.Text.Json.JsonSerializer.Serialize(observations[0]));
            Assert.Equal(1, agent.LastModelCallCount);
            return expected.Rows.Count;
        }

        var permitted = await RunAsync();
        Assert.True(permitted > 0);
        await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        var revoked = await RunAsync();
        Assert.Equal(0, revoked);
    }

    private sealed class CountClient : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("count", EllaiToolNames.OwnedCaseCount,
                        new Dictionary<string, object?> { ["window_days"] = "30" })])));
            throw new InvalidOperationException("The verified count must not require a second model call.");
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (++_calls == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("count", EllaiToolNames.OwnedCaseCount,
                        new Dictionary<string, object?> { ["window_days"] = "30" })]);
                yield break;
            }
            throw new InvalidOperationException("The verified count must not require a second model call.");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
