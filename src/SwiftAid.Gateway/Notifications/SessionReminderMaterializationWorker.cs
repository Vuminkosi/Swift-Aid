using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwiftAid.Application.Sessions;

namespace SwiftAid.Gateway.Notifications;

/// <summary>Continuously expands due public-session schedules into durable recipient work.</summary>
public sealed class SessionReminderMaterializationWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<SessionReminderMaterializationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ISessionReminderDeliveryStore>();
                var created = await store.MaterializeDueAsync(
                    timeProvider.GetUtcNow(), BatchSize, stoppingToken).ConfigureAwait(false);
                if (created > 0)
                {
                    logger.LogInformation("Materialized {DeliveryCount} due session reminder deliveries.", created);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogWarning(
                    "Session reminder materialization failed. Failure type: {FailureType}",
                    exception.GetType().Name);
            }

            await Task.Delay(PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
