using Microsoft.Extensions.Options;
using SwiftAid.Application.Abstractions;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Gateway.Ingestion;

/// <summary>Fair, bounded polling of both existing ingestion lanes; each replica claims with database fencing.</summary>
public sealed class IngestionOutboxWorker(
    IServiceScopeFactory scopes, IOptions<NatsOptions> options, ILogger<IngestionOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || !options.Value.IngestionDispatchEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatched = false;
                foreach (var kind in new[] { "intake", "case" })
                {
                    await using var scope = scopes.CreateAsyncScope();
                    dispatched |= await scope.ServiceProvider.GetRequiredService<IngestionDispatchService>()
                        .DispatchOneAsync(kind, stoppingToken);
                }

                await using (var scope = scopes.CreateAsyncScope())
                {
                    if (scope.ServiceProvider.GetRequiredService<IOptions<SwiftAid.Infrastructure.Classification.ClassificationOptions>>().Value.Enabled)
                        dispatched |= await scope.ServiceProvider.GetRequiredService<SwiftAid.Infrastructure.Classification.ClassificationOutboxDispatcher>()
                            .DispatchOneAsync(stoppingToken);
                }

                if (!dispatched) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogWarning("Ingestion dispatch unavailable. Failure type: {FailureType}", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
