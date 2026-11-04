using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiReasoningRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("framework")]
    public void DefaultAndExplicitFrameworkSelectFrameworkExecutor(string? selection)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new EllaiConsentService());
        services.AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()));
        services.AddScoped(_ => new EllaiPromptBuilder("Fixture policy", "test"));
        services.AddSingleton<EllaiUsageLedger>();
        services.AddSingleton<IEllaiUsageStore, InMemoryEllaiUsageStore>();
        services.AddEllaiReasoningExecutor(new EllaiOptions { Enabled = true, ApiKey = "fixture", ApiUrl = "https://fixture.invalid", ReasoningModel = "fixture" }, selection);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<EllaiStructuredFrameworkAgentFactory>(provider.GetRequiredService<IEllaiFrameworkAgentFactory>());
        using var scope = provider.CreateScope();
        Assert.IsType<EllaiFrameworkExecutor>(scope.ServiceProvider.GetRequiredService<IEllaiReasoningExecutor>());
    }

    [Fact]
    public async Task ConfiguredSpendingThresholdDoesNotStopProviderDispatch()
    {
        var options = new EllaiOptions { Enabled = true, ApiKey = "fixture", ApiUrl = "https://fixture.invalid",
            ReasoningModel = "fixture", SpendingCapDollars = 0m, PricingInputPer1KTokens = 1m };
        var services = new ServiceCollection();
        var warnings = new ThresholdLogger();
        services.AddSingleton<ILogger<EllaiUsageLedger>>(warnings);
        services.AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()));
        services.AddScoped(_ => new EllaiPromptBuilder("Fixture policy", "test"));
        services.AddSingleton<IEllaiUsageStore, InMemoryEllaiUsageStore>();
        services.AddEllaiUsageAccounting(options);
        services.AddEllaiReasoningExecutor(options, "framework");
        using var client = new CountingChatClient();
        services.AddKeyedSingleton<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey, client);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IEllaiReasoningExecutor>();
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>());
        var outcome = await executor.RunAsync(new EllaiLoopRequest(context, "hello", new EllaiRoleOverlay("ask", "help"),
            "none", 100, "cycle", 1), default);
        Assert.True(outcome.Completed);
        Assert.Equal(1, client.Calls);
        Assert.Contains(warnings.Messages, message => message.Contains("spend", StringComparison.Ordinal));
    }

    private sealed class ThresholdLogger : ILogger<EllaiUsageLedger>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Messages.Add(formatter(state, exception));
        }
    }

    private sealed class CountingChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Hello.","spokenText":"Hello.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Provider should not be called.");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void UsageAccountingUsesConfiguredCapAndPrices()
    {
        var services = new ServiceCollection();
        services.AddEllaiUsageAccounting(new EllaiOptions { SpendingCapDollars = 5m,
            PricingInputPer1KTokens = 1m, PricingOutputPer1KTokens = 2m, PricingAudioPerMinute = 3m });
        using var provider = services.BuildServiceProvider();
        var ledger = provider.GetRequiredService<EllaiUsageLedger>();
        Assert.Equal(5m, ledger.TotalSpendCap);
        Assert.Equal(5m, ledger.EnvelopeSpendCap);
        Assert.Equal(new EllaiPriceTable(1m, 2m, 3m), ledger.Prices);
        using var allowed = ledger.TryBeginModelCall("session", 2000);
        Assert.NotNull(allowed);
        using var overThreshold = ledger.TryBeginModelCall("other", 1000);
        Assert.NotNull(overThreshold);
    }

    [Fact]
    public void UsageAccountingRejectsNegativeConfiguration()
    {
        Assert.Throws<MetaConfigurationException>(() => new ServiceCollection().AddEllaiUsageAccounting(
            new EllaiOptions { SpendingCapDollars = -1m }));
        Assert.Throws<MetaConfigurationException>(() => new ServiceCollection().AddEllaiUsageAccounting(
            new EllaiOptions { PricingInputPer1KTokens = -1m }));
    }

    [Fact]
    public void ExplicitlyEnabledServerlessRouteSelectsFrameworkExecutor()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new EllaiConsentService());
        services.AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()));
        services.AddScoped(_ => new EllaiPromptBuilder("Fixture policy", "test"));
        services.AddSingleton<EllaiUsageLedger>();
        services.AddSingleton<IEllaiUsageStore, InMemoryEllaiUsageStore>();
        services.AddEllaiReasoningExecutor(new EllaiOptions { Enabled = true, ReasoningBackend = "serverless",
            ServerlessEnabled = true, ServerlessEndpoint = "https://fixture.invalid/v1", ServerlessApiKey = "fixture",
            ServerlessModel = "fixture" }, "framework");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<EllaiFrameworkExecutor>(scope.ServiceProvider.GetRequiredService<IEllaiReasoningExecutor>());
        Assert.IsType<EllaiFrameworkAgentFactory>(provider.GetRequiredService<IEllaiFrameworkAgentFactory>());
    }

    [Fact]
    public void ConfiguredOllamaRouteSelectsFrameworkExecutor()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()));
        services.AddScoped(_ => new EllaiPromptBuilder("Fixture policy", "test"));
        services.AddSingleton<EllaiUsageLedger>();
        services.AddSingleton<IEllaiUsageStore, InMemoryEllaiUsageStore>();
        services.AddEllaiReasoningExecutor(new EllaiOptions { Enabled = true, ReasoningBackend = "ollama",
            OllamaEndpoint = "http://127.0.0.1:11434", OllamaModel = "fixture" }, "framework");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<EllaiFrameworkExecutor>(scope.ServiceProvider.GetRequiredService<IEllaiReasoningExecutor>());
        Assert.IsType<EllaiFrameworkAgentFactory>(provider.GetRequiredService<IEllaiFrameworkAgentFactory>());
    }

    [Fact]
    public void InvalidSelectionAndUnsupportedProviderFailClosed()
    {
        var options = new EllaiOptions { Enabled = true, ApiKey = "fixture", ApiUrl = "https://fixture.invalid", ReasoningModel = "fixture" };
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddEllaiReasoningExecutor(options, "unknown"));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddEllaiReasoningExecutor(options, "legacy"));
        Assert.Throws<MetaConfigurationException>(() => new ServiceCollection().AddEllaiReasoningExecutor(new EllaiOptions(), "framework"));
        Assert.Throws<MetaConfigurationException>(() => new ServiceCollection().AddEllaiReasoningExecutor(
            new EllaiOptions { Enabled = true, ReasoningBackend = "ollama", OllamaEndpoint = "", OllamaModel = "fixture" }, "framework"));
        Assert.Throws<MetaConfigurationException>(() => new ServiceCollection().AddEllaiReasoningExecutor(
            new EllaiOptions { Enabled = true, ReasoningBackend = "serverless", ServerlessEndpoint = "https://fixture.invalid",
                ServerlessApiKey = "fixture", ServerlessModel = "fixture" }, "framework"));
    }
}
