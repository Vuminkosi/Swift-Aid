using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using SwiftAid.Application.Jobs;
using SwiftAid.Contracts.Jobs;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Messaging;

namespace SwiftAid.Integration.Tests;

public sealed class JobPublisherConfigurationTests
{
    [Fact]
    public void GatewayAppsettingsBindsJobPublisherOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwiftAidInfrastructure(LoadGatewayConfiguration());

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<JobPublisherOptions>>().Value;

        Assert.Equal(JobPublisherOptions.ExpectedStreamName, options.StreamName);
        Assert.Equal(65536, options.MaximumMessageBytes);
    }

    [Fact]
    public async Task EnabledNatsResolvesJetStreamJobPublisherWithoutConnectingToProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(GetGatewayAppsettingsPath(), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nats:Enabled"] = "true",
                ["Nats:Url"] = "tls://nats.invalid:4222",
                ["Nats:Username"] = "synthetic-user",
                ["Nats:Password"] = "synthetic-password"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwiftAidInfrastructure(configuration);

        await using var serviceProvider = services.BuildServiceProvider();

        Assert.IsType<JetStreamPublisher>(serviceProvider.GetRequiredService<IJobPublisher>());
    }

    [Fact]
    public async Task DisabledNatsResolvesPublisherAsUnavailableWithoutRegisteringClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwiftAidInfrastructure(LoadGatewayConfiguration());

        using var serviceProvider = services.BuildServiceProvider();
        var publisher = serviceProvider.GetRequiredService<IJobPublisher>();

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(INatsClient));
        var receipt = await publisher.PublishAsync(CreateJobEnvelope(), CancellationToken.None);

        Assert.Equal(JobPublishStatus.Unavailable, receipt.Status);
        Assert.Equal("job-publisher-not-configured", receipt.FailureCode);
        Assert.Null(receipt.StreamName);
        Assert.Null(receipt.StreamSequence);
    }

    [Theory]
    [InlineData("Nats:Jobs:StreamName", "SWIFTAID_OTHER")]
    [InlineData("Nats:Jobs:MaximumMessageBytes", "0")]
    [InlineData("Nats:Jobs:MaximumMessageBytes", "65537")]
    public void InvalidJobPublisherOptionsFailRegistrationWithoutConnectingToNats(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(GetGatewayAppsettingsPath(), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        var exception = Assert.Throws<OptionsValidationException>(
            () => services.AddSwiftAidInfrastructure(configuration));

        Assert.Contains("Nats:Jobs", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration LoadGatewayConfiguration() =>
        new ConfigurationBuilder()
            .AddJsonFile(GetGatewayAppsettingsPath(), optional: false)
            .Build();

    private static JobEnvelope CreateJobEnvelope() => new(
        JobEnvelope.CurrentSchemaVersion,
        Guid.Parse("9160e07e-1385-4b30-995b-eae5fd2651db"),
        Guid.Parse("a8671879-8132-4f46-a689-628fe3791d57"),
        JobType.CaseSummary,
        "demo-zw",
        Guid.Parse("d31a5e8b-0445-4134-894c-57519ce031b9"),
        Guid.Parse("081a93cf-1353-43b7-ae5d-dd045786a527"),
        new DateTimeOffset(2026, 10, 25, 7, 0, 0, TimeSpan.Zero));

    private static string GetGatewayAppsettingsPath()
    {
        foreach (var start in new[]
                 {
                     new DirectoryInfo(Directory.GetCurrentDirectory()),
                     new DirectoryInfo(AppContext.BaseDirectory)
                 })
        {
            for (DirectoryInfo? directory = start; directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "src",
                    "SwiftAid.Gateway",
                    "appsettings.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException("Could not locate the Gateway appsettings.json from the test directory.");
    }
}
