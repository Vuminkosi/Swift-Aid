using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Demo;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Demo;

/// <summary>
/// Runs the development demo dataset import once at startup. The import runs
/// only when <c>DemoData:Enabled</c> is true, the host is not Production, and the
/// case pipeline (hence the database and aggregate query) is registered.
/// Production always skips, even if the flag is set.
/// </summary>
public sealed class DemoDataStartupImporter(
    IServiceScopeFactory scopeFactory,
    DemoDataOptions options,
    ILogger<DemoDataStartupImporter>? logger = null) : IHostedService
{
    private readonly ILogger<DemoDataStartupImporter> _logger =
        logger ?? NullLogger<DemoDataStartupImporter>.Instance;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Enabled)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var hostEnvironment = scope.ServiceProvider.GetService<IHostEnvironment>();
        if (hostEnvironment is null)
        {
            _logger.LogWarning(
                "The demo import is skipped because the host environment is unknown.");
            return;
        }

        if (hostEnvironment.IsProduction())
        {
            _logger.LogCritical(
                "DemoData:Enabled is set in the Production environment. The demo import is refused.");
            return;
        }

        var dbContext = scope.ServiceProvider.GetService<SwiftAidDbContext>();
        var importer = scope.ServiceProvider.GetService<IDemoCaseImporter>();
        var aggregateQuery = scope.ServiceProvider.GetService<IMapAggregateQuery>();
        if (dbContext is null || importer is null || aggregateQuery is null)
        {
            _logger.LogWarning(
                "The demo import is enabled but the case pipeline is not registered. Skipping demo data import.");
            return;
        }

        var report = await importer.ImportAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Demo data import finished: {Inserted} inserted of {Total} records ({Invalid} invalid, alreadySeeded: {AlreadySeeded}).",
            report.Inserted,
            report.TotalRecords,
            report.Invalid,
            report.AlreadySeeded);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
