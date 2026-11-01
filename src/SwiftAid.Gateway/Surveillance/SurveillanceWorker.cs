using System.Diagnostics;
using Microsoft.Extensions.Options;
using SwiftAid.Infrastructure.Surveillance;

namespace SwiftAid.Gateway.Surveillance;

public sealed class SurveillanceWorker(IServiceScopeFactory scopes, IOptions<SurveillanceOptions> options,
    IHostEnvironment environment, ILogger<SurveillanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        options.Value.Validate();
        if (environment.IsProduction()) throw new InvalidOperationException("Synthetic surveillance rules cannot run in Production.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var started = Stopwatch.GetTimestamp();
                var processed = await scope.ServiceProvider.GetRequiredService<SurveillanceProcessor>().ProcessOneAsync(stoppingToken);
                if (processed) logger.LogInformation("Surveillance work attempt finished in {ElapsedMilliseconds} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                else await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                logger.LogWarning("Surveillance consumer unavailable: {FailureType}", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
