using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Abstractions;
using SwiftAid.Application.Notifications;
using SwiftAid.Domain.Notifications;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Gateway.Notifications;

/// <summary>
/// Dispatches durable PostgreSQL notification outbox rows. Provider acceptance is not a claim of
/// device delivery; retries remain bounded by the storage lease/attempt policy.
/// </summary>
public sealed class NotificationDeliveryWorker : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private const int BatchSize = 25;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly NatsOptions _natsOptions;
    private readonly ILogger<NotificationDeliveryWorker> _logger;

    public NotificationDeliveryWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<NatsOptions> natsOptions,
        ILogger<NotificationDeliveryWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _natsOptions = natsOptions?.Value ?? throw new ArgumentNullException(nameof(natsOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_natsOptions.Enabled)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
                if (processed == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogWarning(
                    "Notification outbox polling failed. Failure type: {FailureType}",
                    exception.GetType().Name);
                await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var outbox = services.GetRequiredService<INotificationDeliveryOutbox>();
        var now = _timeProvider.GetUtcNow();
        var leases = await outbox.ClaimDueAsync(
            now,
            LeaseDuration,
            BatchSize,
            cancellationToken).ConfigureAwait(false);

        foreach (var lease in leases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DispatchAsync(services, lease, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogWarning(
                    "Notification delivery failed. Failure type: {FailureType}",
                    exception.GetType().Name);
                await RetryAsync(outbox, lease, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        return leases.Count;
    }

    private async Task DispatchAsync(
        IServiceProvider services,
        NotificationDeliveryLease lease,
        CancellationToken cancellationToken)
    {
        var outbox = services.GetRequiredService<INotificationDeliveryOutbox>();
        var timeProvider = _timeProvider;
        var now = timeProvider.GetUtcNow();

        var authorizer = services.GetRequiredService<ITrustedNotificationRecipientAuthorizer>();
        var authorization = await authorizer.AuthorizeCurrentAsync(
            lease.RecipientActorId,
            lease.ResourceKind,
            lease.ResourceId,
            lease.Action,
            lease.RecipientRequirement,
            cancellationToken).ConfigureAwait(false);

        if (authorization == TrustedNotificationAuthorizationStatus.Suppressed)
        {
            await AcknowledgeAsync(outbox, lease, timeProvider, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (authorization == TrustedNotificationAuthorizationStatus.Unavailable)
        {
            await RetryAsync(outbox, lease, timeProvider, cancellationToken).ConfigureAwait(false);
            return;
        }

        var publisher = services.GetRequiredService<IInAppNotificationPublisher>();
        var publish = await publisher.PublishAsync(
            new InAppNotification(
                lease.NotificationId,
                lease.RecipientActorId,
                InAppNotificationKind.CaseUpdateAvailable,
                lease.AcceptedEventId,
                now),
            cancellationToken).ConfigureAwait(false);

        if (publish.Status is InAppNotificationPublishStatus.Unavailable or
            InAppNotificationPublishStatus.Failed or
            InAppNotificationPublishStatus.Cancelled or
            InAppNotificationPublishStatus.NotConfigured)
        {
            await RetryAsync(outbox, lease, timeProvider, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await SendPushToActiveDevicesAsync(services, lease, cancellationToken).ConfigureAwait(false))
        {
            await RetryAsync(outbox, lease, timeProvider, cancellationToken).ConfigureAwait(false);
            return;
        }

        await AcknowledgeAsync(outbox, lease, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> SendPushToActiveDevicesAsync(
        IServiceProvider services,
        NotificationDeliveryLease lease,
        CancellationToken cancellationToken)
    {
        var deviceStore = services.GetRequiredService<IPushDeviceStore>();
        var devices = await deviceStore
            .GetActiveForRecipientAsync(lease.RecipientActorId, cancellationToken)
            .ConfigureAwait(false);
        if (devices.Count == 0)
        {
            return true;
        }

        var authorizer = services.GetRequiredService<ITrustedNotificationRecipientAuthorizer>();
        var sender = services.GetRequiredService<IPushNotificationSender>();
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Refresh the installation/token immediately before sending. A device that has been
            // revoked or refreshed since the initial enumeration is never sent its stale token.
            var currentDevices = await deviceStore
                .GetActiveForRecipientAsync(lease.RecipientActorId, cancellationToken)
                .ConfigureAwait(false);
            var currentDevice = currentDevices.FirstOrDefault(candidate =>
                candidate.InstallationId == device.InstallationId);
            if (currentDevice is null)
            {
                continue;
            }

            var authorization = await authorizer.AuthorizeCurrentAsync(
                lease.RecipientActorId,
                lease.ResourceKind,
                lease.ResourceId,
                lease.Action,
                lease.RecipientRequirement,
                cancellationToken).ConfigureAwait(false);

            if (authorization == TrustedNotificationAuthorizationStatus.Suppressed)
            {
                return true;
            }

            if (authorization == TrustedNotificationAuthorizationStatus.Unavailable)
            {
                return false;
            }

            var result = await sender.SendAsync(
                new PushNotificationRequest(
                    currentDevice.Token,
                    PushNotificationKind.CaseUpdateAvailable,
                    lease.NotificationId,
                    currentDevice.Platform,
                    currentDevice.SoundEnabled),
                cancellationToken).ConfigureAwait(false);

            if (result.Status is PushNotificationStatus.Failed or PushNotificationStatus.Cancelled)
            {
                return false;
            }
        }

        return true;
    }

    private async Task AcknowledgeAsync(
        INotificationDeliveryOutbox outbox,
        NotificationDeliveryLease lease,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var result = await outbox.AcknowledgeAsync(
            lease.OutboxId,
            lease.LeaseId,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (result != NotificationDeliveryOutboxUpdateStatus.Updated)
        {
            _logger.LogInformation(
                "Notification delivery acknowledgement was not applied. Status: {Status}",
                result);
        }
    }

    private async Task RetryAsync(
        INotificationDeliveryOutbox outbox,
        NotificationDeliveryLease lease,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var seconds = Math.Min(3600, 5 * Math.Pow(2, Math.Clamp(lease.AttemptCount - 1, 0, 10)));
        var result = await outbox.RetryAsync(
            lease.OutboxId,
            lease.LeaseId,
            timeProvider.GetUtcNow(),
            TimeSpan.FromSeconds(seconds),
            cancellationToken).ConfigureAwait(false);
        if (result != NotificationDeliveryOutboxUpdateStatus.Updated)
        {
            _logger.LogInformation(
                "Notification delivery retry was not applied. Status: {Status}",
                result);
        }
    }
}
