using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiMemoryRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferenceIndexAndProjectionFollowValidatedEnabledFlag(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Qdrant:Enabled"] = enabled.ToString(),
            ["Qdrant:Endpoint"] = "https://qdrant.example.invalid",
            ["Qdrant:ApiKey"] = "synthetic-key"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwiftAidInfrastructure(configuration);
        EllaiMemoryRegistration.AddIfEnabled(services, configuration);

        Assert.Equal(enabled, services.Any(item => item.ServiceType == typeof(IEllaiExactReferenceIndex)));
        Assert.Equal(enabled, services.Any(item => item.ServiceType == typeof(IHostedService) &&
            item.ImplementationType == typeof(EllaiMemoryProjectionWorker)));
        using var provider = services.BuildServiceProvider();
        Assert.Equal(enabled, provider.GetRequiredService<IOptions<QdrantOptions>>().Value.Enabled);
    }

    [Fact]
    public void EnabledIndexRejectsUnvalidatedEndpointAndCredentials()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Qdrant:Enabled"] = "true",
            ["Qdrant:Endpoint"] = "http://remote.example.invalid"
        }).Build();
        var services = new ServiceCollection();

        Assert.Throws<OptionsValidationException>(() => services.AddSwiftAidInfrastructure(configuration));
    }
}
