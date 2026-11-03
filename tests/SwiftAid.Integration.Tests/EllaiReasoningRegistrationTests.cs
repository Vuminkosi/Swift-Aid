using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiReasoningRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("legacy")]
    public void DefaultAndLegacySelectExistingLoop(string? selection)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new EllaiConsentService());
        services.AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()));
        services.AddScoped<EllaiCycleRunner>();
        services.AddScoped(_ => new EllaiPromptBuilder("Fixture policy", "test"));
        services.AddSingleton<IEllaiTurnCompletionClient, UnusedCompletion>();
        services.AddEllaiReasoningExecutor(new EllaiOptions(), selection);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<EllaiReasoningLoop>(scope.ServiceProvider.GetRequiredService<IEllaiReasoningExecutor>());
    }

    [Fact]
    public void InvalidSelectionAndUnsupportedProviderFailClosed()
    {
        var options = new EllaiOptions { Enabled = true, ApiKey = "fixture", ApiUrl = "https://fixture.invalid", ReasoningModel = "fixture" };
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddEllaiReasoningExecutor(options, "unknown"));
        Assert.Throws<MetaConfigurationException>(() => new ServiceCollection().AddEllaiReasoningExecutor(new EllaiOptions(), "framework"));
        Assert.Throws<MetaConfigurationException>(() => new ServiceCollection().AddEllaiReasoningExecutor(
            new EllaiOptions { Enabled = true, ReasoningBackend = "ollama", ApiKey = "fixture", ApiUrl = "https://fixture.invalid", ReasoningModel = "fixture" }, "framework"));
    }

    private sealed class UnusedCompletion : IEllaiTurnCompletionClient
    {
        public Task<EllaiTurnCompletion> CompleteAsync(IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
