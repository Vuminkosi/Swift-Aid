using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Memory;

namespace SwiftAid.Gateway.Ellai;

public static class EllaiMemoryRegistration
{
    // Infrastructure registration validates these options before this method is called.
    public static void AddIfEnabled(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(QdrantOptions.SectionName);
        if (!section.GetValue<bool>(nameof(QdrantOptions.Enabled)))
        {
            return;
        }

        services.AddEllaiReferenceIndex(section[nameof(QdrantOptions.Endpoint)]!, section[nameof(QdrantOptions.ApiKey)]!);
        services.AddHostedService<EllaiMemoryProjectionWorker>();
    }
}
