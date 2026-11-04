using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Sessions;

namespace SwiftAid.Gateway.Notifications;

/// <summary>Dispatches leased session reminders after rechecking current consent and session state.</summary>
public sealed class SessionReminderDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<SessionReminderDeliveryOptions> options,
    ILogger<SessionReminderDeliveryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private readonly SessionReminderDeliveryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channels = new List<string>(2);
        if (_options.Enabled && _options.EmailEnabled) channels.Add("Email");
        if (_options.Enabled && _options.MobilePushEnabled) channels.Add("MobilePush");
        if (channels.Count == 0)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<SessionReminderDeliveryDispatcher>();
                var processed = await dispatcher.DispatchAsync(
                    timeProvider.GetUtcNow(), channels, stoppingToken).ConfigureAwait(false);
                if (processed == 0)
                    await Task.Delay(IdleDelay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogWarning(
                    "Session reminder delivery polling failed. Failure type: {FailureType}",
                    exception.GetType().Name);
                await Task.Delay(IdleDelay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
