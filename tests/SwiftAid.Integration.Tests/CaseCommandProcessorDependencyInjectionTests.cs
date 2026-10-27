using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

public sealed class CaseCommandProcessorDependencyInjectionTests
{
    [Fact]
    public void DisabledCommandProcessingDoesNotRegisterDatabaseOrCommandServices()
    {
        var services = new ServiceCollection();
        services.AddSwiftAidInfrastructure(Configuration(new Dictionary<string, string?>
        {
            ["DataProtection:Enabled"] = "not-a-boolean",
            ["DataProtection:KeysPath"] = "not-a-key-ring-path"
        }));

        using var serviceProvider = services.BuildServiceProvider();

        Assert.Null(serviceProvider.GetService<ICaseCommandProcessor>());
        Assert.Null(serviceProvider.GetService<ICaseCommandUnitOfWork>());
        Assert.Null(serviceProvider.GetService<SwiftAidDbContext>());
        Assert.Null(serviceProvider.GetService<ProtectedPayloadDataProtectionProvider>());
        Assert.Empty(serviceProvider.GetServices<IHostedService>());
        Assert.Null(serviceProvider.GetService<IScopeService>());
        Assert.Null(services.SingleOrDefault(descriptor => descriptor.ServiceType == typeof(SwiftAidDbContext)));
    }

    [Fact]
    public void EnabledCommandProcessingRequiresDatabaseConnectionString()
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<OptionsValidationException>(() =>
            services.AddSwiftAidInfrastructure(Configuration(new Dictionary<string, string?>
            {
                ["CaseCommandProcessing:Enabled"] = "true"
            })));

        Assert.Contains("ConnectionStrings:SwiftAid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledCommandProcessingRequiresProtectedPayloads()
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<OptionsValidationException>(() =>
            services.AddSwiftAidInfrastructure(Configuration(new Dictionary<string, string?>
            {
                ["CaseCommandProcessing:Enabled"] = "true",
                ["ConnectionStrings:SwiftAid"] = "Host=127.0.0.1;Port=1;Database=swiftaid_di_test;Username=test"
            })));

        Assert.Contains("DataProtection:Enabled", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandProcessingAttemptLimitIsValidatedWhenDisabled()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddSwiftAidInfrastructure(Configuration(new Dictionary<string, string?>
            {
                ["CaseCommandProcessing:MaximumTransactionAttempts"] = "4"
            })));

        Assert.Contains("between one and three", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnabledCommandProcessingRejectsInvalidPersistentKeyRingConfigurationAtStartup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwiftAidInfrastructure(
            Configuration(new Dictionary<string, string?>
            {
                ["CaseCommandProcessing:Enabled"] = "true",
                ["ConnectionStrings:SwiftAid"] = "Host=127.0.0.1;Port=1;Database=swiftaid_di_test;Username=test",
                ["DataProtection:Enabled"] = "true",
                ["DataProtection:ApplicationName"] = "SwiftAid.Integration.Tests",
                ["DataProtection:KeysPath"] = "relative-key-path",
                ["DataProtection:MaximumPayloadBytes"] = "1024",
                ["DataProtection:AllowUnencryptedDevelopmentKeys"] = "true"
            }),
            new TestHostEnvironment { EnvironmentName = Environments.Development });

        using var serviceProvider = services.BuildServiceProvider();
        var startupValidator = Assert.Single(serviceProvider.GetServices<IHostedService>());
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => startupValidator.StartAsync(CancellationToken.None));

        Assert.Contains("DataProtection:KeysPath", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnabledCommandProcessingResolvesOneScopedLeaseGraphAndDisposesKeyRingProvider()
    {
        var keyRoot = PrivateKeyRingTestDirectory.CreateRoot("swiftaid-command-di");
        try
        {
            var keyDirectory = PrivateKeyRingTestDirectory.CreateKeyDirectory(keyRoot);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSwiftAidInfrastructure(Configuration(new Dictionary<string, string?>
            {
                ["CaseCommandProcessing:Enabled"] = "true",
                ["CaseCommandProcessing:MaximumTransactionAttempts"] = "2",
                ["ConnectionStrings:SwiftAid"] = "Host=127.0.0.1;Port=1;Database=swiftaid_di_test;Username=test",
                ["DataProtection:Enabled"] = "true",
                ["DataProtection:ApplicationName"] = "SwiftAid.Integration.Tests",
                ["DataProtection:KeysPath"] = keyDirectory,
                ["DataProtection:MaximumPayloadBytes"] = "1024",
                ["DataProtection:AllowUnencryptedDevelopmentKeys"] = "true"
            }), new TestHostEnvironment { EnvironmentName = Environments.Development });

            var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

            ProtectedPayloadDataProtectionProvider dataProtectionProvider;
            try
            {
                var hostedService = Assert.Single(serviceProvider.GetServices<IHostedService>());
                await hostedService.StartAsync(CancellationToken.None);

                dataProtectionProvider = serviceProvider.GetRequiredService<ProtectedPayloadDataProtectionProvider>();
                await using var firstScope = serviceProvider.CreateAsyncScope();
                var first = firstScope.ServiceProvider;
                var unitOfWork = first.GetRequiredService<ICaseCommandUnitOfWork>();

                Assert.Same(unitOfWork, first.GetRequiredService<ICaseCommandLeaseAccessor>());
                Assert.NotNull(first.GetRequiredService<ICaseCommandProcessor>());
                Assert.NotNull(first.GetRequiredService<ICaseCommandPersistence>());
                Assert.NotNull(first.GetRequiredService<IProtectedCaseEventPayloadStore>());
                Assert.IsType<CaseAccessPolicy>(first.GetRequiredService<ICaseAccessPolicy>());
                Assert.NotNull(first.GetRequiredService<ICurrentAccessGrantStore>());
                Assert.NotNull(first.GetRequiredService<ICaseAccessDescriptorStore>());
                Assert.Null(first.GetService<IScopeService>());

                await using var secondScope = serviceProvider.CreateAsyncScope();
                Assert.NotSame(
                    unitOfWork,
                    secondScope.ServiceProvider.GetRequiredService<ICaseCommandUnitOfWork>());
                Assert.Equal(
                    ServiceLifetime.Scoped,
                    Assert.Single(services, descriptor => descriptor.ServiceType == typeof(SwiftAidDbContext)).Lifetime);
            }
            finally
            {
                await serviceProvider.DisposeAsync();
            }

            Assert.Throws<ObjectDisposedException>(() => dataProtectionProvider.CreateProtector("disposal-check"));
        }
        finally
        {
            PrivateKeyRingTestDirectory.DeleteRoot(keyRoot);
        }
    }

    private static IConfiguration Configuration(IDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "SwiftAid.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
