using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using SwiftAid.Application.Abstractions;
using SwiftAid.Contracts.Events;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Infrastructure.Messaging;

public sealed class JetStreamEventPublisher : IEventPublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly INatsJSContext _jetStream;
    private readonly NatsOptions _options;
    private readonly ILogger<JetStreamEventPublisher> _logger;

    public JetStreamEventPublisher(
        INatsClient client,
        IOptions<NatsOptions> options,
        ILogger<JetStreamEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        _jetStream = client.CreateJetStreamContext();
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<EventPublishResult> PublishAsync<TPayload>(
        CaseEventEnvelope<TPayload> eventEnvelope,
        CancellationToken cancellationToken)
        where TPayload : CaseEventPayload
    {
        ArgumentNullException.ThrowIfNull(eventEnvelope);
        if (cancellationToken.IsCancellationRequested)
        {
            return new EventPublishResult(EventPublishStatus.Cancelled);
        }

        if (eventEnvelope.EventId == Guid.Empty || !Enum.IsDefined(eventEnvelope.EventType))
        {
            return new EventPublishResult(EventPublishStatus.Failed, FailureCode: "invalid-event");
        }

        var subject = $"{_options.EventSubjectPrefix}.{eventEnvelope.EventType.ToString().ToLowerInvariant()}";
        var payload = JsonSerializer.SerializeToUtf8Bytes(eventEnvelope, SerializerOptions);
        try
        {
            var acknowledgement = await _jetStream.PublishAsync(
                subject,
                payload,
                opts: new NatsJSPubOpts
                {
                    ExpectedStream = _options.EventStream,
                    MsgId = eventEnvelope.EventId.ToString("N")
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            acknowledgement.EnsureStored();
            if (!string.Equals(acknowledgement.Stream, _options.EventStream, StringComparison.Ordinal))
            {
                _logger.LogWarning("JetStream acknowledged a case event on an unexpected stream.");
                return new EventPublishResult(EventPublishStatus.Failed, FailureCode: "unexpected-stream-ack");
            }

            return new EventPublishResult(
                EventPublishStatus.AcknowledgedByJetStream,
                acknowledgement.Stream,
                acknowledgement.Seq);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EventPublishResult(EventPublishStatus.Cancelled);
        }
        catch (NatsException exception)
        {
            _logger.LogWarning("JetStream event publication is unavailable. Failure type: {FailureType}", exception.GetType().Name);
            return new EventPublishResult(EventPublishStatus.Unavailable, FailureCode: "jetstream-unavailable");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning("JetStream event publication failed. Failure type: {FailureType}", exception.GetType().Name);
            return new EventPublishResult(EventPublishStatus.Failed, FailureCode: "event-publication-failure");
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
