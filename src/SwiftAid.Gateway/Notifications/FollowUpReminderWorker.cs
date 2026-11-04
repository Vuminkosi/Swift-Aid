using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwiftAid.Application.Notifications;

namespace SwiftAid.Gateway.Notifications;

/// <summary>Polls the durable case projection and materializes due reminders.</summary>
public sealed class FollowUpReminderWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<FollowUpReminderWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<FollowUpReminderDispatcher>();
                await dispatcher.DispatchAsync(timeProvider.GetUtcNow(), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogWarning("Follow-up reminder polling failed. Failure type: {FailureType}", exception.GetType().Name);
            }

            await Task.Delay(PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
