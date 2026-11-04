using System.Text.Json;
using System.Security.Claims;
using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class EllaiImportToolsPostgresTests
{
    [IntakePostgresFact]
    public async Task Live_broker_reads_owner_imports_with_current_scope_and_never_returns_source_text()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var linked = await SharedWorkspaceImportTests.AddOrganizationAsync(fixture.Db);
        var importId = Guid.NewGuid();
        var imported = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, importId, CaseImportPostgresTests.Context,
            1, true, new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
        Assert.Equal(1, imported.Summary!.Accepted);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), imports: fixture.Store, importWorkspaces: fixture.Store);
        var context = Context(CaseImportPostgresTests.Actor);
        var destinations = await broker.InvokeAsync(context, new(EllaiToolNames.ImportDestinations, new Dictionary<string, string>()), default);
        Assert.True(destinations.Allowed);
        Assert.Contains("workspace-1", destinations.ResultSummary);
        Assert.DoesNotContain(linked.Organization, destinations.ResultSummary);

        foreach (var change in new[] { false, true })
        {
            using var model = new DestinationClient();
            var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
                broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
            var work = new EllaiUtteranceWork(context.SessionId, context.ScopeEpoch, "List import destinations.",
                context.FeatureId, null, EllaiMode.Ask, 1, change ? "changed" : "current", CaseImportPostgresTests.Actor,
                true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
                EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { EllaiToolNames.ImportDestinations },
                EllaiConsentService.PublicProductHelpPurpose, "turn");
            var calls = 0;
            var turn = await EllaiUtteranceRunner.RunOneAsync(work, new EllaiUtteranceServices(executor, null, null,
                new EllaiConsentService(), InvokeToolAsync: async (toolContext, toolRequest, ct) =>
                    await broker.InvokeAsync(++calls >= 2 && change ? toolContext with { Actor = linked.Actor } : toolContext,
                        toolRequest, ct)), 1024, default);
            Assert.Equal(!change, turn.Completed);
            Assert.True(calls >= 2);
            Assert.Contains("workspace-1", model.ToolResultText);
            Assert.DoesNotContain(linked.Organization, model.ToolResultText);
            if (change) Assert.Null(turn.DisplayText);
            else
            {
                var trace = EllaiToolTrace.SerializeFlattened(turn.ObservedTools, false, []);
                Assert.NotNull(trace);
                Assert.DoesNotContain("workspace-1", trace);
            }
        }

        var history = await broker.InvokeAsync(context, new(EllaiToolNames.Imports, new Dictionary<string, string>
        { ["workspace_id"] = "workspace-1", ["organization_id"] = "org-zw-1", ["country"] = "ZW" }), default);
        Assert.True(history.Allowed);
        Assert.Contains(importId.ToString("D"), history.ResultSummary);
        var request = new EllaiToolRequest(EllaiToolNames.ImportStatus, new Dictionary<string, string> { ["import_id"] = importId.ToString("D") });
        var status = await broker.InvokeAsync(context, request, default);
        Assert.True(status.Allowed);
        Assert.Equal("ImportJob", Assert.Single(status.Evidence).SourceKind);
        Assert.DoesNotContain("Original supplied concern", status.ResultSummary);
        using var document = JsonDocument.Parse(status.ResultSummary);
        Assert.Equal(1, document.RootElement.GetProperty("import").GetProperty("Accepted").GetInt64());
        Assert.Equal("Completed", document.RootElement.GetProperty("import").GetProperty("State").GetString());
        Assert.Equal("not-reported-by-import-ledger", document.RootElement.GetProperty("intelligenceStatus").GetString());

        var other = await broker.InvokeAsync(Context(linked.Actor), request, default);
        Assert.False(other.Allowed);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, other.ReasonCode);
        var staleActor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "officer-1"), new Claim(ActorContext.AccessEpochClaimType, "6")], "fixture")));
        var stale = await broker.InvokeAsync(Context(staleActor), request, default);
        Assert.False(stale.Allowed);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, stale.ReasonCode);
        var injected = await broker.InvokeAsync(context, request with
        { Arguments = new Dictionary<string, string> { ["import_id"] = importId.ToString("D"), ["actor_id"] = linked.Actor.ActorId } }, default);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, injected.ReasonCode);
        Assert.False((await broker.InvokeAsync(context with { RemainingCalls = 0 }, request, default)).Allowed);
        Assert.False((await broker.InvokeAsync(context with { Actor = null }, request, default)).Allowed);
        Assert.False((await broker.InvokeAsync(context with { AllowedTools = new HashSet<string> { EllaiToolNames.ProductFacts } }, request, default)).Allowed);
    }

    private static EllaiToolContext Context(ActorContext actor) => new("import-session", "import-scope", "public.product-help", null, 4, true,
        Actor: actor, DataClassification: EllaiProcessingClassification.UserProvidedNonSensitive,
        ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose);

    private sealed class DestinationClient : IChatClient
    {
        private int _calls;
        public string ToolResultText { get; private set; } = string.Empty;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("destinations", EllaiToolNames.ImportDestinations, new Dictionary<string, object?>())])));
            var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            Assert.Contains(results, result => result.CallId == "destinations");
            ToolResultText = JsonSerializer.Serialize(results);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.","spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
