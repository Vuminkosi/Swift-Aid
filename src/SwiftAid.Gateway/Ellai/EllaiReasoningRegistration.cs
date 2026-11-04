using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Registers the framework-native Ellai reasoning executor.</summary>
public static class EllaiReasoningRegistration
{
    public const string FrameworkClientKey = "ellai-framework";

    public static IServiceCollection AddEllaiUsageAccounting(this IServiceCollection services, EllaiOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (options.SpendingCapDollars < 0 || options.PricingInputPer1KTokens < 0 ||
            options.PricingOutputPer1KTokens < 0 || options.PricingAudioPerMinute < 0)
            throw new MetaConfigurationException("Ellai spending cap and pricing must be nonnegative.");
        var prices = new EllaiPriceTable(options.PricingInputPer1KTokens, options.PricingOutputPer1KTokens,
            options.PricingAudioPerMinute);
        services.AddSingleton(sp => new EllaiUsageLedger(
            envelopeSpendCap: Math.Min(EllaiUsageLedger.DefaultEnvelopeSpendCap, options.SpendingCapDollars),
            totalSpendCap: options.SpendingCapDollars, prices: prices, enforceLimits: false,
            thresholdWarning: threshold => sp.GetService<ILogger<EllaiUsageLedger>>()?.LogWarning(
                "Ellai usage exceeded the configured {Threshold} threshold; execution will continue.", threshold)));
        return services;
    }

    public static IServiceCollection AddEllaiReasoningExecutor(
        this IServiceCollection services, EllaiOptions options, string? executionEngine,
        bool enableKnowledgeSpecialist = false, bool enableCaseSpecialist = false, bool enableAnalyticsSpecialist = false, bool enableUiSpecialist = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        var selection = string.IsNullOrWhiteSpace(executionEngine) ? "framework" : executionEngine.Trim();
        if (!selection.Equals("framework", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ellai execution engine must be 'framework'.");
        if (options.UsesOllamaBackend)
        {
            if (!options.HasOllamaRoute)
                throw new MetaConfigurationException("The Ellai Ollama reasoning route is not configured.");
            services.AddKeyedSingleton<IChatClient>(FrameworkClientKey, (_, _) => EllaiMuseChatClientFactory.CreateOllama(options));
        }
        else if (options.UsesServerlessBackend)
        {
            if (!options.HasServerlessRoute)
                throw new MetaConfigurationException("The Ellai serverless reasoning route is not configured or enabled.");
            services.AddKeyedSingleton<IChatClient>(FrameworkClientKey, (_, _) => EllaiMuseChatClientFactory.CreateServerless(options));
        }
        else if (!options.HasReasoningRoute)
            throw new MetaConfigurationException("The Ellai framework path requires a configured Meta reasoning route.");
        else
            services.AddKeyedSingleton<IChatClient>(FrameworkClientKey, (_, _) => EllaiMuseChatClientFactory.Create(options));
        services.AddSingleton<IEllaiFrameworkAgentFactory, EllaiFrameworkAgentFactory>();
        services.AddScoped<IEllaiReasoningExecutor>(sp => new EllaiFrameworkExecutor(
            sp.GetRequiredKeyedService<IChatClient>(FrameworkClientKey), sp.GetRequiredService<EllaiPromptBuilder>(),
            sp.GetRequiredService<EllaiToolBroker>(), sp.GetRequiredService<EllaiUsageLedger>(),
            sp.GetRequiredService<IEllaiUsageStore>(), agentFactory: sp.GetRequiredService<IEllaiFrameworkAgentFactory>(),
            enableKnowledgeSpecialist: enableKnowledgeSpecialist, enableCaseSpecialist: enableCaseSpecialist,
            enableAnalyticsSpecialist: enableAnalyticsSpecialist, enableUiSpecialist: enableUiSpecialist,
            enableGeospatialSpecialist: sp.GetService<IConfiguration>()?.GetValue<bool>("Ellai:Specialists:Geospatial:Enabled") == true,
            enableWorkflowSpecialist: sp.GetService<IConfiguration>()?.GetValue<bool>("Ellai:Specialists:Workflow:Enabled") == true,
            enableCommunitySessionsSpecialist: sp.GetService<IConfiguration>()?.GetValue<bool>("Ellai:Specialists:CommunitySessions:Enabled") == true));
        return services;
    }
}
