using System.Security.Claims;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Geography;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Intelligence.Meta;
using SwiftAid.Infrastructure.Intake.Geography;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class EllaiLocationReplayTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task RunnerRechecksExactProtectedSearchIncludingEmptyResults(bool empty, bool changed, bool streaming)
    {
        var actor = Actor("location-owner");
        var provider = new EphemeralDataProtectionProvider();
        var protector = new EllaiLocationReplayProtector(provider);
        var registry = new Registry(empty, changed);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), geography: registry,
            locationReplayProtector: protector);
        using var model = new LocationClient();
        var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
            broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var work = new EllaiUtteranceWork("location-session", "scope", "Find a place.", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { EllaiToolNames.Locations },
            EllaiConsentService.PublicProductHelpPurpose, "turn");
        var published = new List<string>();
        var turn = await EllaiUtteranceRunner.RunOneAsync(work,
            new EllaiUtteranceServices(executor, null, null, new EllaiConsentService(),
                OnTextDelta: streaming ? (text, _) => { published.Add(text); return ValueTask.CompletedTask; } : null,
                InvokeToolAsync: broker.InvokeAsync,
                LocationReplayProtector: protector), 1024, default);
        Assert.Equal(!changed, turn.Completed);
        Assert.True(registry.Calls >= 2);
        Assert.All(registry.Queries, query => Assert.Equal("private-search-phrase", query));
        if (changed) { Assert.Null(turn.DisplayText); Assert.Empty(published); return; }
        if (streaming) Assert.NotEmpty(published);
        var observation = Assert.Single(turn.ObservedTools!);
        var scope = Assert.IsType<EllaiLocationReplayScope>(observation.LocationReplayScope);
        var trace = EllaiToolTrace.SerializeFlattened(turn.ObservedTools, false, []);
        Assert.NotNull(trace);
        Assert.DoesNotContain("private-search-phrase", trace);
        Assert.DoesNotContain("private-search-phrase", JsonSerializer.Serialize(observation));
        Assert.Null(EllaiToolTrace.SerializeFlattened([observation with { ToolName = EllaiToolNames.Now }], false, []));
        Assert.Null(EllaiToolTrace.SerializeFlattened([observation with
            { LocationReplayScope = scope with { ResultDigest = "invalid" } }], false, []));
        var context = new EllaiToolContext(work.SessionId, work.ScopeEpoch, work.FeatureId, null, 4, true, Actor: actor,
            DataClassification: EllaiProcessingClassification.UserProvidedNonSensitive,
            AllowedTools: new HashSet<string> { EllaiToolNames.Locations }, ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose);
        Assert.Equal("private-search-phrase", protector.Unprotect(context, scope.ProtectedArguments)!["q"]);
        Assert.Equal("private-search-phrase", new EllaiLocationReplayProtector(provider).Unprotect(context, scope.ProtectedArguments)!["q"]);
        Assert.Null(protector.Unprotect(context with { SessionId = "other-session" }, scope.ProtectedArguments));
        Assert.Null(protector.Unprotect(context with { Actor = Actor("other-owner") }, scope.ProtectedArguments));
        Assert.Null(new EllaiLocationReplayProtector(new EphemeralDataProtectionProvider()).Unprotect(context, scope.ProtectedArguments));
        var tampered = Convert.FromBase64String(scope.ProtectedArguments);
        tampered[^1] ^= 1;
        Assert.Null(protector.Unprotect(context, Convert.ToBase64String(tampered)));
        var turns = new[]
        {
            new EllaiConversationTurn(work.SessionId, "prior", 1, "Find a place", "Done.", "Completed", 1, DateTimeOffset.UtcNow)
                { ToolTraceJson = trace },
            new EllaiConversationTurn(work.SessionId, "current", 2, "Follow up", null, "Processing", 1, null)
        };
        var history = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", actor, null, default,
            toolContext: context, invokeTool: broker.InvokeAsync, locationReplayProtector: new EllaiLocationReplayProtector(provider));
        Assert.Contains("prior", history.RetainedTurnIds);
        var missingKey = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", actor, null, default,
            toolContext: context, invokeTool: broker.InvokeAsync,
            locationReplayProtector: new EllaiLocationReplayProtector(new EphemeralDataProtectionProvider()));
        Assert.DoesNotContain("prior", missingKey.RetainedTurnIds);
        var noProtector = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", actor, null, default,
            toolContext: context, invokeTool: broker.InvokeAsync);
        Assert.DoesNotContain("prior", noProtector.RetainedTurnIds);
        var oldTrace = EllaiToolTrace.SerializeFlattened([observation with { LocationReplayScope = null }], false, []);
        var oldTurns = new[] { turns[0] with { ToolTraceJson = oldTrace }, turns[1] };
        var oldHistory = await EllaiAuthorizedConversationContext.BuildAsync(oldTurns, "current", actor, null, default,
            toolContext: context, invokeTool: broker.InvokeAsync, locationReplayProtector: protector);
        Assert.DoesNotContain("prior", oldHistory.RetainedTurnIds);
        var direct = await broker.InvokeAsync(context, new EllaiToolRequest(EllaiToolNames.Locations,
            new Dictionary<string, string> { ["q"] = "private-search-phrase", ["country"] = "ZW" }), default);
        Assert.NotNull(direct.LocationReplayScope);
        Assert.DoesNotContain(direct.LocationReplayScope.ProtectedArguments, JsonSerializer.Serialize(direct));
    }

    [IntakePostgresFact]
    public async Task RunnerWithholdsAnswerWhenPostgresRegistryChanges()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var importer = new GeographicRegistryImporter(fixture.Db, TimeProvider.System, NullLogger<GeographicRegistryImporter>.Instance);
        var registry = new GeographicRegistryService(fixture.Db, importer, NullLogger<GeographicRegistryService>.Instance);
        var initial = await registry.SearchAsync("Rusape", "ZW", 5, default);
        Assert.NotEmpty(initial.Results);
        var actor = Actor("postgres-location-owner");
        var protector = new EllaiLocationReplayProtector(new EphemeralDataProtectionProvider());
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), geography: registry,
            locationReplayProtector: protector);
        using var model = new LocationClient("Rusape");
        var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
            broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var work = new EllaiUtteranceWork("postgres-location", "scope", "Find Rusape.", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { EllaiToolNames.Locations },
            EllaiConsentService.PublicProductHelpPurpose, "turn");
        var calls = 0;
        try
        {
            var turn = await EllaiUtteranceRunner.RunOneAsync(work, new EllaiUtteranceServices(executor, null, null,
                new EllaiConsentService(), InvokeToolAsync: async (context, request, ct) =>
                {
                    if (++calls == 2)
                        await fixture.Db.GeographicLocations.Where(item => item.LocationId == initial.Results[0].Id)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false), ct);
                    return await broker.InvokeAsync(context, request, ct);
                }, LocationReplayProtector: protector), 1024, default);
            Assert.False(turn.Completed);
            Assert.Null(turn.DisplayText);
            Assert.True(calls >= 2);
        }
        finally
        {
            await fixture.Db.GeographicLocations.Where(item => item.LocationId == initial.Results[0].Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, true));
        }
    }

    [Fact]
    public async Task GatewayWorkerUsesProtectedReplayAndPersistsTrace()
    {
        var actor = Actor("worker-location-owner");
        var store = new InMemoryEllaiSessionStore();
        var session = new EllaiSessionRecord("worker-location", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "Find a place.", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { EllaiToolNames.Locations },
            EllaiConsentService.PublicProductHelpPurpose, "turn");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, work.ClientUtteranceId,
            default, EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId));
        var consent = new EllaiConsentService();
        var protector = new EllaiLocationReplayProtector(new EphemeralDataProtectionProvider());
        var registry = new Registry(false, false);
        using var model = new LocationClient();
        var registrations = new ServiceCollection()
            .AddLogging().AddSingleton<IEllaiSessionStore>(store).AddSingleton<IEllaiUtteranceOutboxStore>(store)
            .AddSingleton<EllaiSessionResponseStore>().AddSingleton(consent).AddSingleton<IEllaiLocationReplayProtector>(protector)
            .AddSingleton<EllaiUsageLedger>().AddSingleton<IEllaiUsageStore, InMemoryEllaiUsageStore>()
            .AddScoped(_ => new EllaiPromptBuilder("Synthetic policy", "fixture"))
            .AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), consent, geography: registry,
                locationReplayProtector: protector));
        registrations.AddEllaiReasoningExecutor(new EllaiOptions
        {
            Enabled = true, ApiKey = "synthetic", ApiUrl = "https://fixture.invalid", ReasoningModel = "fixture"
        }, "framework");
        registrations.AddKeyedSingleton<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey, model);
        await using var services = registrations.BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(10, timeout.Token);
                turn = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token));
            } while (turn.CompletedAtUtc is null);
            Assert.Equal("Completed", turn.Outcome);
            Assert.NotNull(turn.ToolTraceJson);
            Assert.DoesNotContain("private-search-phrase", turn.ToolTraceJson);
            Assert.True(EllaiToolTrace.TryRead(turn.ToolTraceJson, out var trace));
            Assert.NotNull(Assert.Single(trace!.Observations).LocationReplayScope);
            Assert.True(registry.Calls >= 2);
        }
        finally { await worker.StopAsync(default); }
    }

    private static ActorContext Actor(string id) => ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id), new Claim(ActorContext.AccessEpochClaimType, "1")], "fixture")));

    private sealed class Registry(bool empty, bool changed) : IGeographicRegistry
    {
        public int Calls { get; private set; }
        public List<string> Queries { get; } = [];
        public Task<GeoSearchResponse> SearchAsync(string query, string? countryCode, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add(query);
            Calls++;
            var hasResult = empty ? changed && Calls > 1 : !(changed && Calls > 1);
            GeoSearchResult[] results = hasResult
                ? [new GeoSearchResult("ZW:place:rusape", "Rusape", GeoLocationKind.Settlement, "ZW", "Zimbabwe", "Manicaland", null, null, false, [])]
                : [];
            return Task.FromResult(new GeoSearchResponse("registry-test/1", results.Length, results));
        }
        public Task<GeoChildrenResponse> GetChildrenAsync(string parentId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class LocationClient(string query = "private-search-phrase") : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("location", EllaiToolNames.Locations,
                        new Dictionary<string, object?> { ["q"] = query, ["country"] = "ZW" })])));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.","spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""")));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Messages[0].Contents);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
