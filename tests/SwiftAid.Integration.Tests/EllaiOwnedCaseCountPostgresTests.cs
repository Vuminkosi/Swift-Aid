using Microsoft.Extensions.AI;
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

        async Task<(string Observation, int Expected)> RunAsync()
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
            var decision = await agent.RunAsync(context, "Synthetic policy", [new ChatMessage(ChatRole.User, "Count my cases")], 200,
                _ => Task.FromResult(true), broker.InvokeAsync,
                new EllaiFrameworkUsage(context.SessionId, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default);
            Assert.NotNull(decision);
            Assert.Contains("\"allowed\":true", model.Observation);
            Assert.Contains($"{expected.Rows.Count} managed case(s)", model.Observation);
            Assert.DoesNotContain(fixture.CaseId.ToString("D"), model.Observation);
            return (model.Observation, expected.Rows.Count);
        }

        var permitted = await RunAsync();
        Assert.True(permitted.Expected > 0);
        await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        var revoked = await RunAsync();
        Assert.Equal(0, revoked.Expected);
    }

    private sealed class CountClient : IChatClient
    {
        private int _calls;
        public string Observation { get; private set; } = "";
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("count", EllaiToolNames.OwnedCaseCount,
                        new Dictionary<string, object?> { ["window_days"] = "30" })])));
            Observation = string.Join(" ", messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Select(content => content.Result?.ToString()));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.","spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
