using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using SwiftAid.Application.Abstractions;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Infrastructure.Messaging;

public sealed class JetStreamInAppNotificationPublisher : IInAppNotificationPublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly INatsJSContext _jetStream;
    private readonly NatsOptions _options;
    private readonly ILogger<JetStreamInAppNotificationPublisher> _logger;

    public JetStreamInAppNotificationPublisher(
        INatsClient client,
        IOptions<NatsOptions> options,
        ILogger<JetStreamInAppNotificationPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        _jetStream = client.CreateJetStreamContext();
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<InAppNotificationPublishResult> PublishAsync(
        InAppNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (cancellationToken.IsCancellationRequested)
        {
            return new InAppNotificationPublishResult(InAppNotificationPublishStatus.Cancelled);
        }

        if (notification.NotificationId == Guid.Empty ||
            notification.RelatedEventId == Guid.Empty ||
            string.IsNullOrWhiteSpace(notification.RecipientActorId) ||
            notification.CreatedAtUtc.Offset != TimeSpan.Zero ||
            !Enum.IsDefined(notification.Kind))
        {
            return new InAppNotificationPublishResult(
                InAppNotificationPublishStatus.Failed,
                "invalid-notification");
        }

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(notification, SerializerOptions);
            var acknowledgement = await _jetStream.PublishAsync(
                _options.NotificationSubject,
                payload,
                opts: new NatsJSPubOpts
                {
                    ExpectedStream = _options.NotificationStream,
                    MsgId = notification.NotificationId.ToString("N")
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            acknowledgement.EnsureStored();
            if (!string.Equals(acknowledgement.Stream, _options.NotificationStream, StringComparison.Ordinal))
            {
                _logger.LogWarning("JetStream acknowledged an in-app notification on an unexpected stream.");
                return new InAppNotificationPublishResult(
                    InAppNotificationPublishStatus.Failed,
                    "unexpected-stream-ack");
            }

            return new InAppNotificationPublishResult(InAppNotificationPublishStatus.AcknowledgedByJetStream);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new InAppNotificationPublishResult(InAppNotificationPublishStatus.Cancelled);
        }
        catch (NatsException exception)
        {
            _logger.LogWarning("JetStream notification publication is unavailable. Failure type: {FailureType}", exception.GetType().Name);
            return new InAppNotificationPublishResult(
                InAppNotificationPublishStatus.Unavailable,
                "jetstream-unavailable");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning("JetStream notification publication failed. Failure type: {FailureType}", exception.GetType().Name);
            return new InAppNotificationPublishResult(
                InAppNotificationPublishStatus.Failed,
                "notification-publication-failure");
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
