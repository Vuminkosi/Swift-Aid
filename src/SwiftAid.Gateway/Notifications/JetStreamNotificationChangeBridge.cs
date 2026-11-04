using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using SwiftAid.Application.Authorization;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Gateway.Notifications;

/// <summary>
/// Bridges each process's JetStream notification consumer to authenticated local SSE waiters.
/// The stream is server-only; the client receives empty change hints and refetches its feed.
/// </summary>
public sealed class JetStreamNotificationChangeBridge : BackgroundService, INotificationChangeSignal
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private const int ConsumerMaxAckPending = 16;
    private const int ConsumeBatchSize = 16;

    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NatsOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JetStreamNotificationChangeBridge> _logger;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, TaskCompletionSource>> _waiters = new(StringComparer.Ordinal);

    public JetStreamNotificationChangeBridge(
        IServiceProvider serviceProvider,
        IServiceScopeFactory scopeFactory,
        IOptions<NatsOptions> options,
        TimeProvider timeProvider,
        ILogger<JetStreamNotificationChangeBridge> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<NotificationChangeWaitResult> WaitForChangeAsync(
        ActorContext actor,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (!await IsCurrentActorAsync(actor, cancellationToken).ConfigureAwait(false))
        {
            return NotificationChangeWaitResult.ActorNoLongerAuthorized;
        }

        var actorKey = CreateActorKey(actor.ActorId);
        var actorWaiters = _waiters.GetOrAdd(
            actorKey,
            static _ => new ConcurrentDictionary<Guid, TaskCompletionSource>());
        var waiterId = Guid.NewGuid();
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actorWaiters[waiterId] = waiter;

        try
        {
            var delay = Task.Delay(timeout, cancellationToken);
            await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!await IsCurrentActorAsync(actor, cancellationToken).ConfigureAwait(false))
            {
                return NotificationChangeWaitResult.ActorNoLongerAuthorized;
            }

            return waiter.Task.IsCompleted
                ? NotificationChangeWaitResult.Changed
                : NotificationChangeWaitResult.Heartbeat;
        }
        finally
        {
            actorWaiters.TryRemove(waiterId, out _);
            if (actorWaiters.IsEmpty)
            {
                ((ICollection<KeyValuePair<string, ConcurrentDictionary<Guid, TaskCompletionSource>>>)_waiters)
                    .Remove(new KeyValuePair<string, ConcurrentDictionary<Guid, TaskCompletionSource>>(
                        actorKey,
                        actorWaiters));
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = _serviceProvider.GetRequiredService<INatsClient>();
                await RunConsumerAsync(client, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogWarning(
                    "Notification change consumer is unavailable. Failure type: {FailureType}",
                    exception.GetType().Name);
                await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunConsumerAsync(INatsClient client, CancellationToken cancellationToken)
    {
        var jetStream = client.CreateJetStreamContext();
        var durableName = $"swiftaid_gateway_notifications_{Environment.ProcessId}_{Guid.NewGuid():N}";
        var consumerConfig = new ConsumerConfig(durableName)
        {
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            AckWait = TimeSpan.FromSeconds(_options.NotificationConsumerAckWaitSeconds),
            DeliverPolicy = ConsumerConfigDeliverPolicy.New,
            FilterSubject = _options.NotificationSubject,
            InactiveThreshold = TimeSpan.FromSeconds(_options.NotificationConsumerInactiveThresholdSeconds),
            MaxAckPending = ConsumerMaxAckPending,
            MaxDeliver = _options.NotificationConsumerMaxDeliveries
        };

        INatsJSConsumer? consumer = null;
        try
        {
            consumer = await jetStream.CreateConsumerAsync(
                _options.NotificationStream,
                consumerConfig,
                cancellationToken).ConfigureAwait(false);

            var consumeOptions = new NatsJSConsumeOpts
            {
                DrainOnCancel = true,
                MaxMsgs = ConsumeBatchSize
            };

            await foreach (var message in consumer
                .ConsumeAsync<string>(opts: consumeOptions, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var recipientActorId = message.Data is null
                        ? null
                        : ReadRecipientActorId(message.Data);
                    if (recipientActorId is not null)
                    {
                        NotifyLocalWaiters(recipientActorId);
                    }
                }
                catch (JsonException)
                {
                    // A malformed server event cannot be routed. Acknowledge it to avoid a poison loop.
                }

                await message.AckAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (consumer is not null)
            {
                using var deleteTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await jetStream.DeleteConsumerAsync(
                        _options.NotificationStream,
                        consumer.Info.Name,
                        deleteTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    _logger.LogDebug(
                        "Notification change consumer cleanup was deferred. Failure type: {FailureType}",
                        exception.GetType().Name);
                }
            }
        }
    }

    private void NotifyLocalWaiters(string recipientActorId)
    {
        var actorKey = CreateActorKey(recipientActorId);
        if (!_waiters.TryGetValue(actorKey, out var waiters))
        {
            return;
        }

        foreach (var waiter in waiters.Values)
        {
            waiter.TrySetResult();
        }
    }

    private async ValueTask<bool> IsCurrentActorAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SwiftAidDbContext>();
            var actorState = await (
                    from currentActor in dbContext.Actors.AsNoTracking()
                    join accessState in dbContext.ActorAccessStates.AsNoTracking()
                        on currentActor.ActorId equals accessState.ActorId
                    where currentActor.ActorId == actor.ActorId
                    select new
                    {
                        currentActor.Status,
                        currentActor.ExpiresAtUtc,
                        accessState.AccessEpoch
                    })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return actorState is not null &&
                actorState.Status == PersistentActorStatus.Active &&
                actorState.AccessEpoch == actor.AccessEpoch &&
                (actorState.ExpiresAtUtc is null || actorState.ExpiresAtUtc > _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static string? ReadRecipientActorId(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > 16_384)
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("recipientActorId", out var actorIdElement) ||
            actorIdElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var actorId = actorIdElement.GetString();
        return string.IsNullOrWhiteSpace(actorId) || actorId.Length > 512 ||
            !string.Equals(actorId, actorId.Trim(), StringComparison.Ordinal)
            ? null
            : actorId;
    }

    private static string CreateActorKey(string actorId)
    {
        var actorBytes = Encoding.UTF8.GetBytes(actorId);
        try
        {
            return Convert.ToHexString(SHA256.HashData(actorBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actorBytes);
        }
    }
}
