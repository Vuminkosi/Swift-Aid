using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Registers one reasoning path while keeping the existing path available for rollback.</summary>
public static class EllaiReasoningRegistration
{
    public const string FrameworkClientKey = "ellai-framework";

    public static IServiceCollection AddEllaiReasoningExecutor(
        this IServiceCollection services, EllaiOptions options, string? executionEngine)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        var selection = string.IsNullOrWhiteSpace(executionEngine) ? "legacy" : executionEngine.Trim();
        services.AddScoped<EllaiReasoningLoop>();
        if (selection.Equals("legacy", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IEllaiReasoningExecutor>(sp => sp.GetRequiredService<EllaiReasoningLoop>());
        }
        else if (selection.Equals("framework", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.HasReasoningRoute || options.UsesOllamaBackend || options.UsesServerlessBackend)
                throw new MetaConfigurationException("The Ellai framework path requires a configured Meta reasoning route.");
            services.AddKeyedSingleton<IChatClient>(FrameworkClientKey, (_, _) => EllaiMuseChatClientFactory.Create(options));
            services.AddScoped<IEllaiReasoningExecutor>(sp => new EllaiFrameworkExecutor(
                sp.GetRequiredKeyedService<IChatClient>(FrameworkClientKey), sp.GetRequiredService<EllaiPromptBuilder>(),
                sp.GetRequiredService<EllaiToolBroker>(), sp.GetRequiredService<EllaiUsageLedger>(),
                sp.GetRequiredService<IEllaiUsageStore>()));
        }
        else
        {
            throw new InvalidOperationException("Ellai execution engine must be 'legacy' or 'framework'.");
        }
        return services;
    }
}
