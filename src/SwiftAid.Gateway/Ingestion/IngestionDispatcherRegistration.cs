using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Surveillance;

namespace SwiftAid.Gateway.Ingestion;

/// <summary>Registers JetStream dispatchers only when their transport is enabled.</summary>
public static class IngestionDispatcherRegistration
{
    public static IServiceCollection AddIngestionDispatchers(this IServiceCollection services, bool natsEnabled)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (natsEnabled)
        {
            services.AddScoped<TranslationOutboxDispatcher>();
            services.AddScoped<ClassificationOutboxDispatcher>();
            services.AddScoped<SurveillanceOutboxDispatcher>();
        }
        return services;
    }
}
