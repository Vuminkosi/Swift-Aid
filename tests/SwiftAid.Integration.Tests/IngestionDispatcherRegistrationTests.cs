using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Gateway.Ingestion;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Surveillance;

namespace SwiftAid.Integration.Tests;

public sealed class IngestionDispatcherRegistrationTests
{
    [Fact]
    public void DisabledTransportDoesNotRequireDispatcherDependencies()
    {
        var services = new ServiceCollection();
        services.AddIngestionDispatchers(false);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();
        Assert.Null(scope.ServiceProvider.GetService<TranslationOutboxDispatcher>());
        Assert.Null(scope.ServiceProvider.GetService<ClassificationOutboxDispatcher>());
        Assert.Null(scope.ServiceProvider.GetService<SurveillanceOutboxDispatcher>());
    }

    [Fact]
    public void EnabledTransportRegistersScopedDispatchers()
    {
        var services = new ServiceCollection();
        services.AddIngestionDispatchers(true);

        Assert.Contains(services, x => x.ServiceType == typeof(TranslationOutboxDispatcher) && x.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, x => x.ServiceType == typeof(ClassificationOutboxDispatcher) && x.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, x => x.ServiceType == typeof(SurveillanceOutboxDispatcher) && x.Lifetime == ServiceLifetime.Scoped);
    }
}
