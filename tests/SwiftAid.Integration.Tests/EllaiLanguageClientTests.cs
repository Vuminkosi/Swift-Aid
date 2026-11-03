using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Intelligence.Meta;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiLanguageClientTests
{
    [EllaiLanguageAndMuseLiveFact]
    public async Task FrameworkWorkerRecallsContextAfterTranslatedReply()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EllaiLanguage:Endpoint"] = Environment.GetEnvironmentVariable("SWIFTAID_TEST_LANGUAGE_ENDPOINT"),
            ["EllaiLanguage:InternalKey"] = Environment.GetEnvironmentVariable("SWIFTAID_TEST_LANGUAGE_KEY")
        }).Build();
        using var http = new HttpClient();
        var language = new EllaiLanguageClient(http, configuration);
        using var model = EllaiMuseChatClientFactory.Create(EllaiOptions.Bind(Environment.GetEnvironmentVariable));
        var consent = new EllaiConsentService();
        var executor = new EllaiFrameworkExecutor(model,
            new EllaiPromptBuilder("You are Ellai, the SwiftAid assistant. Follow the final decision schema.", "fixture"),
            new EllaiToolBroker(new EllaiPolicyService(), consent), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "synthetic-language-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "fixture")));
        var store = new InMemoryEllaiSessionStore();
        var session = new EllaiSessionRecord("synthetic-language-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        await using var services = new ServiceCollection()
            .AddSingleton<IEllaiSessionStore>(store).AddSingleton<IEllaiUtteranceOutboxStore>(store)
            .AddSingleton(new EllaiSessionResponseStore()).AddSingleton(consent)
            .AddSingleton<IEllaiLanguageClient>(language).AddSingleton<IEllaiReasoningExecutor>(executor).BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await worker.StartAsync(timeout.Token);
        try
        {
            async Task<EllaiConversationTurn> RunTurnAsync(string id, string text, string outputLanguage)
            {
                var current = (await store.GetAsync(session.SessionId, timeout.Token))!;
                var work = new EllaiUtteranceWork(session.SessionId, "scope", text, "public.product-help", null,
                    EllaiMode.Ask, 1, "cycle-" + id, actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
                    EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp,
                    id, Languages: new("eng", outputLanguage));
                Assert.NotNull(await store.TryAcceptUtteranceAsync(current with
                    { SessionVersion = current.SessionVersion + 1, CyclesUsed = current.CyclesUsed + 1 },
                    current.SessionVersion, id, timeout.Token, EllaiUtteranceWorkEnvelope.Serialize(work, id)));
                EllaiConversationTurn turn;
                do
                {
                    await Task.Delay(100, timeout.Token);
                    turn = (await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token))
                        .Single(item => item.ClientUtteranceId == id);
                } while (turn.CompletedAtUtc is null);
                Assert.Equal("Completed", turn.Outcome);
                return turn;
            }

            var first = await RunTurnAsync("first", "Remember the word cobalt for my next question. Acknowledge briefly.", "sna");
            Assert.False(string.IsNullOrWhiteSpace(first.AssistantText));
            var second = await RunTurnAsync("second", "What word did I ask you to remember? Reply with that word.", "eng");
            Assert.Contains("cobalt", second.AssistantText!, StringComparison.OrdinalIgnoreCase);
            Assert.True(EllaiToolTrace.TryRead(second.ToolTraceJson!, out var trace));
            Assert.True(trace!.HistoryIncluded);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [EllaiLanguageAndMuseLiveFact]
    public async Task RealLanguageServiceAndFrameworkHandleTranslatedConversation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EllaiLanguage:Endpoint"] = Environment.GetEnvironmentVariable("SWIFTAID_TEST_LANGUAGE_ENDPOINT"),
            ["EllaiLanguage:InternalKey"] = Environment.GetEnvironmentVariable("SWIFTAID_TEST_LANGUAGE_KEY")
        }).Build();
        using var http = new HttpClient();
        var language = new EllaiLanguageClient(http, configuration);
        var options = EllaiOptions.Bind(Environment.GetEnvironmentVariable);
        using var model = EllaiMuseChatClientFactory.Create(options);
        var consent = new EllaiConsentService();
        var executor = new EllaiFrameworkExecutor(model,
            new EllaiPromptBuilder("You are Ellai, the SwiftAid assistant. Follow the final decision schema.", "fixture"),
            new EllaiToolBroker(new EllaiPolicyService(), consent), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "synthetic-language-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "fixture")));
        var work = new EllaiUtteranceWork("synthetic-language-session", "scope", "Mhoro", "public.product-help", null,
            EllaiMode.Ask, 1, "language-cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp,
            "language-turn", Languages: new("sna", "sna"));

        var result = await EllaiUtteranceRunner.RunOneAsync(work, new(executor, null, null, consent, Languages: language),
            1024, CancellationToken.None);

        Assert.True(result.Completed, result.StopReason);
        Assert.NotNull(result.Decision);
        Assert.False(string.IsNullOrWhiteSpace(result.Decision.DisplayText));
        Assert.False(string.IsNullOrWhiteSpace(result.DisplayText));
    }

    [EllaiLanguageLiveFact]
    public async Task RealPinnedProviderRespondsThroughGatewayClient()
    {
        using var http = new HttpClient();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EllaiLanguage:Endpoint"] = Environment.GetEnvironmentVariable("SWIFTAID_TEST_LANGUAGE_ENDPOINT"),
            ["EllaiLanguage:InternalKey"] = Environment.GetEnvironmentVariable("SWIFTAID_TEST_LANGUAGE_KEY")
        }).Build();
        var client = new EllaiLanguageClient(http, configuration);
        var translated = await client.TranslateAsync(new("gateway-real-translation", "Good morning.", "eng", "sna"), default);
        Assert.False(string.IsNullOrWhiteSpace(translated.Text));
        Assert.Equal(EllaiLanguageClient.TranslationRevision, translated.ModelRevision);
        var transcription = await client.TranscribeAsync(new("gateway-real-asr", "sna", Convert.ToBase64String(new byte[32000]), 16000), default);
        Assert.Equal(EllaiLanguageClient.TranscriptionRevision, transcription.ModelRevision);
        Assert.Equal("unreviewed", transcription.Quality);
    }

    [Fact]
    public async Task TranslationChecksProviderIdentityAndRetainsUnreviewedStatus()
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new EllaiTranslationResult("turn", "Hello", "sna", "eng",
                EllaiLanguageClient.TranslationModel, EllaiLanguageClient.TranslationRevision, 4, 10, "unreviewed"))
        });
        var result = await Client(handler).TranslateAsync(new("turn", "Mhoro", "sna", "eng"), default);
        Assert.Equal("unreviewed", result.Quality);
        Assert.Equal("Bearer private-test-key", handler.Authorization);
    }

    [Theory]
    [InlineData("stale-turn", "sna", "eng")]
    [InlineData("turn", "nya", "eng")]
    [InlineData("turn", "sna", "tso")]
    public async Task MismatchedProviderResultIsRejected(string request, string source, string target)
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new EllaiTranslationResult(request, "Hello", source, target,
                EllaiLanguageClient.TranslationModel, EllaiLanguageClient.TranslationRevision, 4, 10, "unreviewed"))
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).TranslateAsync(new("turn", "Mhoro", "sna", "eng"), default));
    }

    [Fact]
    public async Task BusyIsRetryableAndUnsupportedLanguageNeverCallsProvider()
    {
        var handler = new Handler(_ => new(HttpStatusCode.TooManyRequests));
        var client = Client(handler);
        var error = await Assert.ThrowsAsync<EllaiLanguageProviderException>(() => client.TranslateAsync(new("turn", "Mhoro", "sna", "eng"), default));
        Assert.True(error.Retryable);
        await Assert.ThrowsAsync<ArgumentException>(() => client.TranslateAsync(new("turn", "text", "nde", "eng"), default));
        Assert.Equal(1, handler.Calls);
    }

    private static EllaiLanguageClient Client(HttpMessageHandler handler) => new(new(handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EllaiLanguage:Endpoint"] = "http://language/",
            ["EllaiLanguage:InternalKey"] = "private-test-key"
        }).Build());

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString();
            Calls++;
            return Task.FromResult(response(request));
        }
    }
}

public sealed class EllaiLanguageAndMuseLiveFactAttribute : FactAttribute
{
    public EllaiLanguageAndMuseLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ELLAI_LIVE") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_LANGUAGE_ENDPOINT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_LANGUAGE_KEY")))
            Skip = "Requires explicitly enabled Muse and a configured local language provider.";
    }
}

public sealed class EllaiLanguageLiveFactAttribute : FactAttribute
{
    public EllaiLanguageLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_LANGUAGE_ENDPOINT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_LANGUAGE_KEY")))
            Skip = "Requires an explicitly configured local pinned language provider and test credential.";
    }
}
