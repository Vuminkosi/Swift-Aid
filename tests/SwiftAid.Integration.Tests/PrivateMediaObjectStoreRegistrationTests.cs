using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftAid.Application.Attachments;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Storage;

namespace SwiftAid.Integration.Tests;

public sealed class PrivateMediaObjectStoreRegistrationTests
{
    [Fact]
    public void EnabledMinioRegistersPrivateMediaObjectStoreWithoutConnectingToProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwiftAidInfrastructure(EnabledMinioConfiguration());

        using var serviceProvider = services.BuildServiceProvider();

        var store = serviceProvider.GetRequiredService<IPrivateMediaObjectStore>();

        Assert.IsType<MinioPrivateMediaObjectStore>(store);
    }

    [Fact]
    public void DisabledMinioDoesNotRegisterPrivateMediaObjectStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwiftAidInfrastructure(new ConfigurationBuilder().Build());

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IPrivateMediaObjectStore));
    }

    private static IConfiguration EnabledMinioConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Minio:Enabled"] = "true",
                ["Minio:Endpoint"] = "https://minio.invalid:9000",
                ["Minio:Bucket"] = "swiftaid-private-media",
                ["Minio:MaxObjectSizeBytes"] = "1048576",
                ["Minio:AccessKey"] = "synthetic-access-key",
                ["Minio:SecretKey"] = "synthetic-secret-key",
                ["Minio:UseSsl"] = "true"
            })
            .Build();
}
