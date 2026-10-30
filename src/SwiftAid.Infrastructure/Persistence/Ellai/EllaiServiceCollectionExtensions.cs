using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

/// <summary>Ellai persistence wiring. PostgreSQL is authoritative for Ellai
/// sessions and checkpoints; the in-memory store remains for tests and
/// local development without a database.</summary>
public static class EllaiServiceCollectionExtensions
{
    public static IServiceCollection AddEllaiPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IEllaiSessionStore, PostgresEllaiSessionStore>();
        services.AddScoped<IEllaiApprovalStore, PostgresEllaiApprovalStore>();
        services.AddScoped<IEllaiTopicStore, PostgresEllaiTopicStore>();
        services.AddScoped<IEllaiConsentStore, PostgresEllaiConsentStore>();
        services.AddScoped<IEllaiUsageStore, PostgresEllaiUsageStore>();
        return services;
    }
}
