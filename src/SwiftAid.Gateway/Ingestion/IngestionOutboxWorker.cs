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
        var retryAfter = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatched = false;
                foreach (var kind in new[] { "intake", "case" })
                {
                    dispatched |= await DispatchLaneAsync(kind, services =>
                        services.GetRequiredService<IngestionDispatchService>().DispatchOneAsync(kind, stoppingToken));
                }

                dispatched |= await DispatchLaneAsync("classification", async services =>
                {
                    if (!services.GetRequiredService<IOptions<SwiftAid.Infrastructure.Classification.ClassificationOptions>>().Value.Enabled)
                        return false;
                    return await services.GetRequiredService<SwiftAid.Infrastructure.Classification.ClassificationOutboxDispatcher>()
                        .DispatchOneAsync(stoppingToken);
                });

                dispatched |= await DispatchLaneAsync("surveillance", services =>
                    services.GetRequiredService<SwiftAid.Infrastructure.Surveillance.SurveillanceOutboxDispatcher>()
                        .DispatchOneAsync(stoppingToken));

                dispatched |= await DispatchLaneAsync("translation", services =>
                    services.GetRequiredService<SwiftAid.Infrastructure.Classification.TranslationOutboxDispatcher>()
                        .DispatchOneAsync(stoppingToken));

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

        async Task<bool> DispatchLaneAsync(string lane, Func<IServiceProvider, Task<bool>> dispatch)
        {
            if (retryAfter.TryGetValue(lane, out var retryAt) && retryAt > DateTimeOffset.UtcNow) return false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await dispatch(scope.ServiceProvider).ConfigureAwait(false);
                retryAfter.Remove(lane);
                return result;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                retryAfter[lane] = DateTimeOffset.UtcNow.AddSeconds(2);
                logger.LogWarning("Ingestion lane {Lane} unavailable. Failure type: {FailureType}", lane, exception.GetType().Name);
                return false;
            }
        }
    }
}
