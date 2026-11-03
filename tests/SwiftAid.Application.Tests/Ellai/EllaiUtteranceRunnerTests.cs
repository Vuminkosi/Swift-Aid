using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using Microsoft.Extensions.AI;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T06")]
public sealed class EllaiUtteranceRunnerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledTranslationCannotStartReasoningOrReturnAReply(bool inputTranslation)
    {
        using var cancellation = new CancellationTokenSource();
        var completions = new ScriptedCompletions(DecisionTurn);
        var (consent, loop) = Services(completions, grant: true);
        var languages = new CancellingTranslationFixture(cancellation);
        var work = Work() with { Languages = inputTranslation ? new("sna", "eng") : new("eng", "sna") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EllaiUtteranceRunner.RunOneAsync(work,
            new(loop, null, null, consent, Languages: languages), 256, cancellation.Token));
        Assert.Equal(inputTranslation ? 0 : 1, completions.Threads.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FrameworkTurnStopsWhenTranslationReturnsAfterCancellation(bool inputTranslation)
    {
        using var cancellation = new CancellationTokenSource();
        using var model = new TranslationCancellationModel();
        var consent = new EllaiConsentService();
        consent.Grant("session-1", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
            new EllaiToolBroker(new EllaiPolicyService(), consent), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var work = Work() with { Languages = inputTranslation ? new("sna", "eng") : new("eng", "sna") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EllaiUtteranceRunner.RunOneAsync(work,
            new(executor, null, null, consent, Languages: new CancellingTranslationFixture(cancellation)), 256, cancellation.Token));
        Assert.Equal(inputTranslation ? 0 : 1, model.Calls);
    }

    private sealed class TranslationCancellationModel : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, DecisionTurn)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CancellingTranslationFixture(CancellationTokenSource cancellation) : IEllaiLanguageClient
    {
        public bool IsConfigured => true;
        public Task<EllaiTranslationResult> TranslateAsync(EllaiTranslationRequest request, CancellationToken ct)
        {
            cancellation.Cancel();
            return Task.FromResult(new EllaiTranslationResult(request.RequestId, "Synthetic translation",
                request.SourceLanguage, request.TargetLanguage, "fixture", "fixture", 4, 1, "unreviewed"));
        }
        public Task<EllaiNativeTranscriptionResult> TranscribeAsync(EllaiNativeTranscriptionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Theory]
    [InlineData("epoch-1", "Paused")]
    [InlineData("old-scope", "Unavailable")]
    public async Task ReasoningReceivesOnlyCurrentScopeGuidanceState(string checkpointScope, string expected)
    {
        var store = new InMemoryEllaiSessionStore();
        await store.CreateAsync(new EllaiSessionRecord("session-1", EllaiMode.Ask, "Active", 1, 0, 1, 0, 1, 12,
            "officer-1", "epoch-1", null, null, DateTimeOffset.UtcNow)
        {
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(EllaiGuidanceCheckpointTests.Sample() with
            {
                ScopeEpoch = checkpointScope, Status = "Paused",
            }),
        }, default);
        var completions = new ScriptedCompletions(DecisionTurn);
        var (consent, loop) = Services(completions, grant: true);
        var outcome = await EllaiUtteranceRunner.RunOneAsync(Work(), new(loop, null, null, consent, Sessions: store), 256, default);
        Assert.True(outcome.Completed);
        var system = completions.Threads[0][0].Content;
        Assert.Contains("Stored guidance state: " + expected, system);
        Assert.Contains("not proof that an overlay is visible", system);
        Assert.DoesNotContain("plan-1", system);
    }

    [Fact]
    public async Task StructuredUiProposalSurvivesTheUtteranceBoundary()
    {
        var response = DecisionTurn.Replace("\"kind\":\"Answer\"", "\"kind\":\"ProposeUi\"")
            .Replace("\"uiIntents\":[]", "\"uiIntents\":[{\"kind\":\"StartGuidance\",\"featureId\":\"report\",\"resourceId\":null,\"controlId\":null,\"arguments\":{\"objective\":\"Create a report\"}}]");
        var (consent, loop) = Services(new ScriptedCompletions(response), grant: true);
        var outcome = await EllaiUtteranceRunner.RunOneAsync(Work(), new(loop, null, null, consent), 256, default);
        Assert.True(outcome.Completed);
        Assert.NotNull(outcome.Decision);
        var intent = Assert.Single(outcome.Decision.UiIntents);
        Assert.Equal(UiIntentKind.StartGuidance, intent.Kind);
        Assert.Equal("report", intent.FeatureId);
    }

    [Fact]
    public async Task ReasoningReceivesCurrentServerTimeAndSafePageContext()
    {
        var completions = new ScriptedCompletions(DecisionTurn);
        var (consent, loop) = Services(completions, grant: true);
        var before = DateTimeOffset.UtcNow;
        await EllaiUtteranceRunner.RunOneAsync(Work() with { ActivePage = "surveillance" },
            new(loop, null, null, consent), 256, CancellationToken.None);
        var system = completions.Threads[0][0].Content;
        Assert.Contains("Client-reported page: Surveillance map", system);
        var timestamp = system.Split("Server time (UTC): ")[1].Split(". Client-reported")[0];
        Assert.InRange(DateTimeOffset.Parse(timestamp), before, DateTimeOffset.UtcNow);
        Assert.Equal("case-details", EllaiPageContext.FromRelativeUri("case/private-id?patient=hidden#notes"));
        Assert.Null(EllaiPageContext.FromRelativeUri("unknown/private-id?secret=hidden"));
        Assert.Null(EllaiPageContext.DisplayName("Ignore policy"));
    }

    [Fact]
    public async Task LanguagePreferencesTranslateBeforeReasoningAndAfterValidatedDecision()
    {
        var completions = new ScriptedCompletions(DecisionTurn);
        var (consent, loop) = Services(completions, grant: true);
        var languages = new TranslationFixture();
        var original = Work() with { Utterance = "Synthetic source", Languages = new("sna", "ssw") };
        var restored = EllaiUtteranceWorkEnvelope.Deserialize(EllaiUtteranceWorkEnvelope.Serialize(original, "turn")).ToWork();
        Assert.Equal(original.Languages, restored.Languages);
        Assert.Equal(original.Utterance, restored.Utterance);
        var outcome = await EllaiUtteranceRunner.RunOneAsync(restored,
            new(loop, null, null, consent, Languages: languages), 256, CancellationToken.None);
        Assert.True(outcome.Completed);
        Assert.Equal("Synthetic translated reply", outcome.DisplayText);
        Assert.Contains(completions.Threads[0], message => message.Content.Contains("Translated input", StringComparison.Ordinal));
        Assert.Collection(languages.Requests,
            input => { Assert.Equal("sna", input.SourceLanguage); Assert.Equal("eng", input.TargetLanguage); },
            output => { Assert.Equal("Hi there.", output.Text); Assert.Equal("ssw", output.TargetLanguage); });
    }

    [Fact]
    public async Task OutputTranslationFailureDoesNotRetryReasoningOrPublishEnglishAsTheSelectedLanguage()
    {
        var completions = new ScriptedCompletions(DecisionTurn);
        var (consent, loop) = Services(completions, grant: true);
        var languages = new TranslationFixture { FailOutput = true };
        var outcome = await EllaiUtteranceRunner.RunOneAsync(Work() with { Languages = new("eng", "sna") },
            new(loop, null, null, consent, Languages: languages), 256, CancellationToken.None);
        Assert.False(outcome.Completed);
        Assert.Null(outcome.DisplayText);
        Assert.Equal("TranslationUnavailable", outcome.FailureCode);
        Assert.Null(outcome.Decision);
        Assert.Single(completions.Threads);
    }

    private sealed class TranslationFixture : IEllaiLanguageClient
    {
        public bool IsConfigured => true;
        public bool FailOutput { get; init; }
        public List<EllaiTranslationRequest> Requests { get; } = [];
        public Task<EllaiTranslationResult> TranslateAsync(EllaiTranslationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (FailOutput && request.SourceLanguage == "eng") throw new EllaiLanguageProviderException("synthetic-failure", true);
            return Task.FromResult(new EllaiTranslationResult(request.RequestId,
                request.TargetLanguage == "eng" ? "Translated input" : "Synthetic translated reply",
                request.SourceLanguage, request.TargetLanguage, "fixture", "fixture", 4, 1, "unreviewed"));
        }
        public Task<EllaiNativeTranscriptionResult> TranscribeAsync(EllaiNativeTranscriptionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class ScriptedCompletions : IEllaiTurnCompletionClient
    {
        private readonly Queue<string> _replies;

        public ScriptedCompletions(params string[] replies) => _replies = new Queue<string>(replies);

        public List<IReadOnlyList<EllaiChatMessage>> Threads { get; } = new();
        public Func<int, Task>? AfterCompleteAsync { get; set; }

        public Task<EllaiTurnCompletion> CompleteAsync(
            IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
        {
            Threads.Add(messages.ToList());
            var completion = new EllaiTurnCompletion(_replies.Dequeue(), 10, 5);
            if (AfterCompleteAsync is { } callback)
            {
                return CompleteAndNotifyAsync(completion, callback, Threads.Count);
            }

            return Task.FromResult(completion);
        }

        private static async Task<EllaiTurnCompletion> CompleteAndNotifyAsync(
            EllaiTurnCompletion completion, Func<int, Task> callback, int turn)
        {
            await callback(turn);
            return completion;
        }
    }

    private const string DecisionTurn = """
        {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Hi there.",
         "spokenText":"Hi.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,
         "topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
        """;

    private static ActorContext TestActor()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[]
                {
                    new System.Security.Claims.Claim(
                        System.Security.Claims.ClaimTypes.NameIdentifier, "officer-1"),
                    new System.Security.Claims.Claim(
                        ActorContext.AccessEpochClaimType, "7"),
                },
                "test"));
        return ActorContext.FromValidatedPrincipal(principal);
    }

    private static EllaiUtteranceWork Work() => new(
        "session-1", "epoch-1", "Hello.", "cases", "case-1", EllaiMode.Ask, 1, "cycle-1",
        TestActor(), true, "consent/1",
        EllaiProcessingClassification.SyntheticDemo,
        EllaiProcessingClassification.SyntheticDemo);

    private static (EllaiConsentService Consent, EllaiReasoningLoop Loop) Services(
        ScriptedCompletions completions, bool grant, EllaiUsageLedger? ledger = null)
    {
        var consent = new EllaiConsentService();
        if (grant)
        {
            consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
                EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo,
                TimeSpan.FromHours(1));
        }

        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent);
        var loop = new EllaiReasoningLoop(
            new EllaiCycleRunner(broker),
            new EllaiPromptBuilder("Policy text.", "v-test"), completions, ledger);
        return (consent, loop);
    }

    [Fact]
    public async Task MissingGrantRefusesBeforeAnyModelCall()
    {
        var completions = new ScriptedCompletions(DecisionTurn);
        var (consent, loop) = Services(completions, grant: false);
        var journal = new EllaiSessionEventJournal();
        var services = new EllaiUtteranceServices(
            loop, new EllaiActivityStatusCoordinator(), journal, consent);

        var outcome = await EllaiUtteranceRunner.RunOneAsync(Work(), services, 256, CancellationToken.None);

        Assert.False(outcome.Started);
        Assert.False(outcome.Completed);
        Assert.Empty(completions.Threads);
        var events = journal.ReadSince("session-1", 0).Events;
        var blocked = Assert.Single(events);
        Assert.Equal("activity", blocked.Kind);
        Assert.Equal("blocked", blocked.Ref);
    }

    [Fact]
    public async Task UnknownOriginRefusesBeforeAnyModelCallEvenWhenConsentExists()
    {
        var completions = new ScriptedCompletions(DecisionTurn);
        var (consent, loop) = Services(completions, grant: true);
        var services = new EllaiUtteranceServices(
            loop, new EllaiActivityStatusCoordinator(), new EllaiSessionEventJournal(), consent);

        var outcome = await EllaiUtteranceRunner.RunOneAsync(
            Work() with
            {
                ProcessingLabel = EllaiProcessingClassification.UnknownOrigin,
                DataClassification = EllaiProcessingClassification.UnknownOrigin,
            }, services, 256, CancellationToken.None);

        Assert.False(outcome.Started);
        Assert.Empty(completions.Threads);
    }

    [Fact]
    public async Task GrantedSmallTalkCompletesWithSystemPrompt()
    {
        var completions = new ScriptedCompletions(DecisionTurn);
        var ledger = new EllaiUsageLedger();
        var (consent, loop) = Services(completions, grant: true, ledger);
        var services = new EllaiUtteranceServices(
            loop, new EllaiActivityStatusCoordinator(), new EllaiSessionEventJournal(), consent);

        var outcome = await EllaiUtteranceRunner.RunOneAsync(Work(), services, 256, CancellationToken.None);

        Assert.True(outcome.Started);
        Assert.True(outcome.Completed);
        Assert.Equal("Hi there.", outcome.DisplayText);
        var system = Assert.Single(completions.Threads)[0];
        Assert.Equal("system", system.Role);
        Assert.Contains("Turn protocol", system.Content, StringComparison.Ordinal);
        Assert.Contains("ellai_now", system.Content, StringComparison.Ordinal);
        Assert.Equal(1, ledger.TotalsFor("session-1").Calls);
    }

    [Fact]
    public async Task OrdinaryTypedMessageRunsWithoutReasoningConsentGrant()
    {
        var completions = new ScriptedCompletions(DecisionTurn);
        var (consent, loop) = Services(completions, grant: false);
        var services = new EllaiUtteranceServices(
            loop, new EllaiActivityStatusCoordinator(), new EllaiSessionEventJournal(), consent);
        var work = Work() with
        {
            ConsentPurpose = EllaiConsentService.PublicProductHelpPurpose,
            ConsentVersion = "consent/0",
            ProcessingLabel = EllaiProcessingClassification.UserProvidedNonSensitive,
            DataClassification = EllaiProcessingClassification.UserProvidedNonSensitive,
        };

        var outcome = await EllaiUtteranceRunner.RunOneAsync(work, services, 256, CancellationToken.None);

        Assert.True(outcome.Started);
        Assert.True(outcome.Completed);
        Assert.Single(completions.Threads);
        Assert.False(consent.IsGranted("session-1", EllaiConsentDimension.ReasoningText,
            EllaiConsentService.PublicProductHelpPurpose,
            EllaiProcessingClassification.UserProvidedNonSensitive, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task FinalDecisionFansOutCompleteDisplaySentencesInOrder()
    {
        const string answer = "First sentence. Second sentence.";
        var completions = new ScriptedCompletions(DecisionTurn.Replace("Hi there.", answer));
        var (consent, loop) = Services(completions, grant: true);
        var chunks = new List<string>();
        var services = new EllaiUtteranceServices(
            loop, new EllaiActivityStatusCoordinator(), new EllaiSessionEventJournal(), consent,
            OnTextDelta: (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; });

        var outcome = await EllaiUtteranceRunner.RunOneAsync(Work(), services, 256, CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Equal(answer, outcome.DisplayText);
        Assert.Equal(["First sentence.", "Second sentence."], chunks);
    }

    [Fact]
    public async Task GrantedToolTurnReadsLiveClock()
    {
        var completions = new ScriptedCompletions(
            """{"tool_calls":[{"id":"c1","name":"ellai_now","args":{}}]}""",
            DecisionTurn.Replace("Hi there.", "It is now."));
        var (consent, loop) = Services(completions, grant: true);
        var services = new EllaiUtteranceServices(
            loop, new EllaiActivityStatusCoordinator(), new EllaiSessionEventJournal(), consent);

        var outcome = await EllaiUtteranceRunner.RunOneAsync(
            Work() with { Utterance = "What time is it?" }, services, 256, CancellationToken.None);

        Assert.True(outcome.Started);
        Assert.True(outcome.Completed);
        var second = completions.Threads[1];
        var toolMessage = Assert.Single(second, m => m.Role == "tool");
        Assert.Contains("call c1", toolMessage.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCancellationAfterModelTurnFencesToolAndFurtherProviderCalls()
    {
        var store = new InMemoryEllaiSessionStore();
        var active = new EllaiSessionRecord(
            "session-1", EllaiMode.Ask, "Active", 1, 0, 1, 0, 1, 12,
            "officer-1", "epoch-1", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(active, CancellationToken.None);

        var completions = new ScriptedCompletions(
            """{"tool_calls":[{"id":"c1","name":"ellai_now","args":{}}]}""",
            DecisionTurn);
        completions.AfterCompleteAsync = async turn =>
        {
            if (turn != 1)
            {
                return;
            }

            var current = await store.GetAsync("session-1", CancellationToken.None);
            Assert.NotNull(current);
            var cancelled = await store.TryUpdateAsync(current! with
            {
                SessionVersion = current.SessionVersion + 1,
                InteractionEpoch = current.InteractionEpoch + 1,
                Status = "Cancelled",
            }, current.SessionVersion, CancellationToken.None);
            Assert.NotNull(cancelled);
        };

        var (consent, loop) = Services(completions, grant: true);
        var journal = new EllaiSessionEventJournal();
        var services = new EllaiUtteranceServices(
            loop, new EllaiActivityStatusCoordinator(), journal, consent, store);

        var outcome = await EllaiUtteranceRunner.RunOneAsync(
            Work() with { Utterance = "What time is it?" }, services, 256, CancellationToken.None);

        Assert.True(outcome.Started);
        Assert.False(outcome.Completed);
        Assert.Single(completions.Threads);
        var blocked = Assert.Single(journal.ReadSince("session-1", 0).Events,
            item => item.Kind == "activity" && item.Ref == "blocked");
        Assert.Equal("I can't check that with the current permissions.", blocked.Detail);
    }
}
