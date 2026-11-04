using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T06")]
public sealed class EllaiReasoningLoopTests
{
    private static readonly EllaiToolContext Context = new(
        "session-1", "epoch-1", "cases", "case-1", 4, true, "consent/1", EllaiProcessingClassification.SyntheticDemo);

    private sealed class ScriptedCompletions : IEllaiTurnCompletionClient
    {
        private readonly Queue<string> _replies;

        public ScriptedCompletions(params string[] replies) => _replies = new Queue<string>(replies);

        public List<IReadOnlyList<EllaiChatMessage>> Threads { get; } = new();

        public int Calls { get; private set; }

        public Task<EllaiTurnCompletion> CompleteAsync(
            IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
        {
            Calls++;
            Threads.Add(messages.ToList());
            return Task.FromResult(new EllaiTurnCompletion(_replies.Dequeue(), 10, 5));
        }
    }

    private static EllaiToolBroker GrantedBroker()
    {
        var consent = new EllaiConsentService();
        consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        return new EllaiToolBroker(new EllaiPolicyService(), consent);
    }

    private static EllaiReasoningLoop Loop(
        ScriptedCompletions completions, EllaiUsageLedger? ledger = null) =>
        new(new EllaiCycleRunner(GrantedBroker()),
            new EllaiPromptBuilder("Policy text.", "v-test"), completions, ledger);

    private static EllaiLoopRequest Request(EllaiToolContext? context = null) => new(
        context ?? Context, "What is the current time?", new EllaiRoleOverlay("ask", "Be brief."),
        "clock", 256, "cycle-1", 1);

    private const string DecisionTurn = """
        {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"It is now.",
         "spokenText":"Now.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,
         "topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
        """;

    [Fact]
    public void AdvisoryUsageThresholdIsNotPresentedAsRemainingTokenLimit()
    {
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 10, enforceLimits: false);
        ledger.Record(new EllaiUsageSample("session-1", 10, 1));
        var prompt = new EllaiExecutionPromptBuilder(new EllaiPromptBuilder("Policy text.", "v-test"), ledger)
            .Build(Request());

        Assert.DoesNotContain("session tokens left", prompt, StringComparison.Ordinal);
        var enforced = new EllaiExecutionPromptBuilder(new EllaiPromptBuilder("Policy text.", "v-test"),
            new EllaiUsageLedger(sessionTokenBudget: 10)).Build(Request());
        Assert.Contains("10 session tokens left", enforced, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameworkPromptRetainsPolicyAndGuidanceButUsesNativeToolProtocol()
    {
        var prompt = new EllaiExecutionPromptBuilder(new EllaiPromptBuilder("Policy text.", "v-test"))
            .Build(Request());
        Assert.Contains("Policy text.", prompt);
        Assert.Contains("## Interactive guidance", prompt);
        Assert.Contains("Use the registered function tools", prompt);
        Assert.Contains("## Final decision JSON schema", prompt);
        Assert.DoesNotContain(EllaiReasoningLoop.TurnProtocol, prompt);
        Assert.Contains("Claims must cite exact evidence", prompt);
        Assert.Contains("Never infer a case count from a partial list", prompt);
        Assert.Contains("ellai_owned_case_count", prompt);
        Assert.Contains("controls=report.subject,report.narrative,report.time-location,report.confirmations", prompt);
        Assert.Contains("NotProvided alone does not mean a field is required", prompt);
        Assert.Contains("omitted, hidden, stale or unavailable input has unknown status", prompt);
    }

    [Fact]
    public async Task OrdinaryConversationDiscoversReportAndProducesGuidanceProposalThroughExistingLoop()
    {
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), uiRegistry: registry);
        var completions = new ScriptedCompletions(
            """{"tool_calls":[{"id":"discover","name":"ellai_ui_catalogue","args":{"feature":"report","goal":"report-help"}}]}""",
            """
            {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Let me show you the report form.",
             "spokenText":"Let me show you the report form.","claims":[],"questions":[],
             "uiIntents":[{"kind":"StartGuidance","featureId":"ellai.guidance","resourceId":null,"controlId":null,
               "arguments":{"feature":"report","objective":"Open the report form"}}],
             "planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
            """);
        var loop = new EllaiReasoningLoop(new EllaiCycleRunner(broker), new EllaiPromptBuilder("Policy text.", "v-test"), completions);
        var context = Context with
        {
            FeatureId = "public.product-help", ResourceId = null,
            ConsentPurpose = EllaiConsentService.PublicProductHelpPurpose,
            DataClassification = EllaiProcessingClassification.UserProvidedNonSensitive,
            AllowedTools = new HashSet<string> { EllaiToolNames.UiCatalogue, EllaiToolNames.UiObserve },
        };
        var result = await loop.RunAsync(Request(context) with { Utterance = "Show me how to report a concern" }, default);
        Assert.True(result.Completed);
        Assert.Equal(UiIntentKind.StartGuidance, Assert.Single(result.Decision!.UiIntents).Kind);
        Assert.Contains(completions.Threads[1], message => message.Role == "tool" && message.Content.Contains("report.entry"));
        Assert.Contains("resourceId=null, controlId=null", completions.Threads[0][0].Content);
    }

    [Fact]
    public async Task ReportStatusQuestionSuppliesObservedInputStatesToReasoningLoop()
    {
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var observations = new InMemoryEllaiUiObservationStore();
        await observations.RecordAsync(new("session-1", "view", "context", "web", ["report.narrative"],
            DateTimeOffset.UtcNow, [new("report.narrative", UiObservationState.Ready)],
            InputStates: [new("report.narrative", "report.original-statement", UiInputPresence.Entered),
                new("report.narrative", "report.other-observations", UiInputPresence.NotProvided)]), default);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(),
            uiRegistry: registry, uiDiscovery: new EllaiUiDiscoveryService(), uiObservations: observations);
        var completions = new ScriptedCompletions(
            """{"tool_calls":[{"id":"observe","name":"ellai_ui_observe","args":{"feature":"report","controls":"report.subject,report.narrative,report.time-location,report.confirmations","view":"current"}}]}""",
            DecisionTurn);
        var context = Context with { FeatureId = "public.product-help", ResourceId = null,
            ConsentPurpose = EllaiConsentService.PublicProductHelpPurpose,
            DataClassification = EllaiProcessingClassification.UserProvidedNonSensitive,
            AllowedTools = new HashSet<string> { EllaiToolNames.UiCatalogue, EllaiToolNames.UiObserve } };
        var loop = new EllaiReasoningLoop(new EllaiCycleRunner(broker), new EllaiPromptBuilder("Policy text.", "v-test"), completions);

        var result = await loop.RunAsync(Request(context) with { Utterance = "What have I entered?" }, default);

        Assert.True(result.Completed);
        var tool = Assert.Single(completions.Threads[1], message => message.Role == "tool");
        Assert.Contains("report.original-statement (Describe what happened): Entered", tool.Content);
        Assert.Contains("report.other-observations (Add other observations): NotProvided", tool.Content);
        Assert.Contains("omitted inputs have unknown status", tool.Content);
    }

    [Theory]
    [InlineData("typed", "typed message")]
    [InlineData("live-voice", "Live Speak")]
    [InlineData("recorded-voice", "recorded voice")]
    public async Task ProviderReceivesInteractionModeAsStyleOnly(string mode, string expected)
    {
        var completions = new ScriptedCompletions(DecisionTurn);
        await Loop(completions).RunAsync(Request() with { InteractionMode = mode }, CancellationToken.None);
        var system = Assert.Single(completions.Threads[0], message => message.Role == "system").Content;
        Assert.Contains("Interaction mode: " + expected, system);
        Assert.Contains("does not constitute approval", system);
    }

    [Fact]
    public async Task ProviderReceivesNestedDecisionSchemaAndReturnedEvidenceReferences()
    {
        var completions = new ScriptedCompletions(
            """{"tool_calls":[{"id":"facts","name":"ellai_productfacts","args":{}}]}""", DecisionTurn);
        var result = await Loop(completions).RunAsync(Request(), CancellationToken.None);
        Assert.True(result.Completed);
        var system = completions.Threads[0][0].Content;
        Assert.Contains("\"evidenceId\"", system);
        Assert.Contains("\"sourceKind\"", system);
        Assert.Contains("\"choiceIds\"", system);
        var observed = Assert.Single(completions.Threads[1], m => m.Role == "tool");
        Assert.Contains("\"evidenceId\"", observed.Content);
        Assert.Contains("\"sourceKind\"", observed.Content);
    }

    [Fact]
    public async Task FabricatedEvidenceCannotCompleteAnOtherwiseValidDecision()
    {
        var fabricated = DecisionTurn.Replace("\"claims\":[]", """
            "claims":[{"claimId":"claim","text":"An invented fact","kind":"ProductFact",
            "evidence":[{"sourceKind":"ProductFact","resourceId":"invented","version":"1","fieldPath":null,"evidenceId":"invented"}]}]
            """);
        var completions = new ScriptedCompletions(fabricated, DecisionTurn);
        var outcome = await Loop(completions).RunAsync(Request(), CancellationToken.None);
        Assert.True(outcome.Completed);
        Assert.Equal(2, completions.Calls);
        Assert.Empty(outcome.Decision!.Claims);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"final_decision\":{\"schemaVersion\":999}}")]
    public async Task RejectedDecisionsCanBeRepairedBeyondThePreviousLimit(string rejected)
    {
        var completions = new ScriptedCompletions(rejected, rejected, rejected, DecisionTurn);
        var outcome = await Loop(completions).RunAsync(Request(), CancellationToken.None);
        Assert.True(outcome.Completed);
        Assert.Equal(4, completions.Calls);
        Assert.True(outcome.RepairUsed);
    }

    [Fact]
    public async Task RepairDoesNotAppendAnotherProvisionalAnswerToTheSameTurn()
    {
        var completions = new ScriptedCompletions(DecisionTurn.Replace("\"claims\":[]", "\"claims\":[null]"), DecisionTurn);
        var deltas = new List<string>();
        var outcome = await Loop(completions).RunAsync(Request(), CancellationToken.None,
            onTextDelta: (text, _) => { deltas.Add(text); return ValueTask.CompletedTask; });
        Assert.True(outcome.Completed);
        Assert.Equal("It is now.", string.Concat(deltas));
        Assert.Equal("It is now.", outcome.Decision!.DisplayText);
    }

    [Fact]
    public async Task ToolResultEntersLaterTurnWithCallId()
    {
        var completions = new ScriptedCompletions(
            """{"tool_calls":[{"id":"c1","name":"ellai_now","args":{}}]}""",
            DecisionTurn);
        var loop = Loop(completions);

        var outcome = await loop.RunAsync(Request(), CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Equal(1, outcome.ToolCallsUsed);
        Assert.Equal(2, completions.Calls);
        var second = completions.Threads[1];
        var toolMessage = Assert.Single(second, m => m.Role == "tool");
        Assert.Equal("c1", toolMessage.ToolCallId);
        Assert.Contains("c1", toolMessage.Content, StringComparison.Ordinal);
        Assert.Contains("ellai_now", toolMessage.Content, StringComparison.Ordinal);
        Assert.Contains("Current server time", toolMessage.Content, StringComparison.Ordinal);
        var assistantMessage = Assert.Single(second, m => m.Role == "assistant");
        Assert.Equal("c1", Assert.Single(assistantMessage.ToolCalls!).CallId);
    }

    [Fact]
    public async Task InvalidFinalDecisionAfterToolObservationGetsARepairTurn()
    {
        const string invalidDecision = """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"A report can be submitted.","spokenText":"","claims":[null],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""";
        var completions = new ScriptedCompletions(
            """{"tool_calls":[{"id":"c1","name":"ellai_now","args":{}}]}""",
            invalidDecision,
            DecisionTurn);
        var loop = Loop(completions);

        var outcome = await loop.RunAsync(Request(), CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Equal(3, completions.Calls);
        Assert.Contains(completions.Threads[2], message => message.Role == "user"
            && message.Content.Contains("Turn rejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestrictedSessionPromptAdvertisesOnlyItsAllowedTools()
    {
        var completions = new ScriptedCompletions(DecisionTurn);
        var loop = Loop(completions);
        var request = Request(Context with
        {
            AllowedTools = new HashSet<string>(StringComparer.Ordinal)
            {
                EllaiToolNames.ProductFacts,
            },
        });

        var outcome = await loop.RunAsync(request, CancellationToken.None);

        Assert.True(outcome.Completed);
        var systemPrompt = Assert.Single(completions.Threads[0], message => message.Role == "system").Content;
        Assert.Contains(EllaiToolNames.ProductFacts, systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(EllaiToolNames.State, systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(EllaiToolNames.Evidence, systemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseFailureRepairsInsideLivingThread()
    {
        var completions = new ScriptedCompletions("not json at all", DecisionTurn);
        var loop = Loop(completions);

        var outcome = await loop.RunAsync(Request(), CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Equal(2, completions.Calls);
        var second = completions.Threads[1];
        Assert.Contains(second, m => m.Role == "user" && m.Content.Contains("Turn rejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BudgetExhaustionStopsWithBudgetDetail()
    {
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 5);
        var completions = new ScriptedCompletions(DecisionTurn);
        var loop = Loop(completions, ledger);

        var outcome = await loop.RunAsync(Request(), CancellationToken.None);

        Assert.False(outcome.Completed);
        Assert.Equal(0, completions.Calls);
        Assert.Contains("authorization", outcome.StopReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReservationIncludesPromptAndCompletionBeforeProviderDispatch()
    {
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 256);
        var completions = new ScriptedCompletions(DecisionTurn);
        var loop = Loop(completions, ledger);

        var outcome = await loop.RunAsync(Request(), CancellationToken.None);

        Assert.False(outcome.Completed);
        Assert.Equal(0, completions.Calls);
        Assert.Contains("authorization", outcome.StopReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UsageAccruesToLedgerPerTurn()
    {
        var ledger = new EllaiUsageLedger();
        var completions = new ScriptedCompletions(DecisionTurn);
        var loop = Loop(completions, ledger);

        var outcome = await loop.RunAsync(Request(), CancellationToken.None);

        Assert.True(outcome.Completed);
        var totals = ledger.TotalsFor("session-1");
        Assert.Equal(1, totals.Calls);
        Assert.Equal(15, totals.TotalTokens);
    }

    [Fact]
    public async Task SmallTalkTurnEmitsNoActivity()
    {
        var completions = new ScriptedCompletions(DecisionTurn);
        var loop = new EllaiReasoningLoop(
            new EllaiCycleRunner(GrantedBroker()),
            new EllaiPromptBuilder("Policy text.", "v-test"), completions);
        var activity = new EllaiActivityStatusCoordinator();
        var journal = new EllaiSessionEventJournal();

        var outcome = await loop.RunAsync(Request(), CancellationToken.None, activity: activity, journal: journal);

        Assert.True(outcome.Completed);
        Assert.Null(activity.CurrentStatus("session-1", 1));
        Assert.Empty(journal.ReadSince("session-1", 0).Events);
    }

    [Fact]
    public async Task InvalidRequestsFailFast()
    {
        var loop = Loop(new ScriptedCompletions());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            loop.RunAsync(Request() with { Utterance = "  " }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            loop.RunAsync(Request() with { MaxTokensPerTurn = 0 }, CancellationToken.None));
    }
}
