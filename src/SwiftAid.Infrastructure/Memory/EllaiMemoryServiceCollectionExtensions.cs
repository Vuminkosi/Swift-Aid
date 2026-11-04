using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Infrastructure.Memory;

public static class EllaiMemoryServiceCollectionExtensions
{
    public static IServiceCollection AddEllaiReferenceIndex(this IServiceCollection services, string endpoint, string apiKey)
    {
        services.AddHttpClient("EllaiReferenceIndex")
            .AddTypedClient((http, _) => new EllaiMemoryIndex(http, endpoint, apiKey, "ellai-memory"));
        services.AddScoped<IEllaiExactReferenceIndex>(provider => provider.GetRequiredService<EllaiMemoryIndex>());
        return services;
    }
}
