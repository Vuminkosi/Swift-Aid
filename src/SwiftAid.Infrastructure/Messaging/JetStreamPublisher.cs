using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using SwiftAid.Application.Jobs;
using SwiftAid.Contracts.Jobs;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Infrastructure.Messaging;

/// <summary>Publishes opaque version-one job envelopes with JetStream acknowledgement and message deduplication.</summary>
public sealed class JetStreamPublisher : IJobPublisher
{
    private readonly IJobMessageTransport _transport;
    private readonly JobPublisherOptions _options;
    private readonly ILogger<JetStreamPublisher> _logger;

    public JetStreamPublisher(
        INatsClient client,
        IOptions<JobPublisherOptions> options,
        ILogger<JetStreamPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        _transport = new JetStreamJobMessageTransport(client.CreateJetStreamContext());
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal JetStreamPublisher(
        IJobMessageTransport transport,
        IOptions<JobPublisherOptions> options,
        ILogger<JetStreamPublisher> logger)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<PublishReceipt> PublishAsync(
        JobEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (cancellationToken.IsCancellationRequested)
        {
            return new PublishReceipt(JobPublishStatus.Cancelled);
        }

        byte[] payload;
        string subject;
        try
        {
            envelope.Validate();
            subject = GetSubject(envelope.JobType);
            payload = envelope.SerializeToUtf8();
        }
        catch (DisabledJobTypeException)
        {
            return new PublishReceipt(JobPublishStatus.Failed, FailureCode: "job-type-disabled");
        }
        catch (ArgumentException)
        {
            return new PublishReceipt(JobPublishStatus.Failed, FailureCode: "invalid-job-envelope");
        }

        if (payload.Length > _options.MaximumMessageBytes)
        {
            return new PublishReceipt(JobPublishStatus.Failed, FailureCode: "job-message-too-large");
        }

        try
        {
            var acknowledgement = await _transport.PublishAsync(
                subject,
                payload,
                _options.StreamName,
                envelope.MessageId.ToString("D"),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!acknowledgement.IsAcknowledged)
            {
                return new PublishReceipt(JobPublishStatus.Failed, FailureCode: "jetstream-not-acknowledged");
            }

            if (!string.Equals(acknowledgement.Stream, _options.StreamName, StringComparison.Ordinal))
            {
                _logger.LogWarning("JetStream acknowledged a job on an unexpected stream.");
                return new PublishReceipt(JobPublishStatus.Failed, FailureCode: "unexpected-stream-ack");
            }

            return new PublishReceipt(
                JobPublishStatus.AcknowledgedByJetStream,
                acknowledgement.Stream,
                acknowledgement.Sequence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PublishReceipt(JobPublishStatus.Cancelled);
        }
        catch (NatsException exception)
        {
            _logger.LogWarning(
                "JetStream job publication is unavailable. Failure type: {FailureType}",
                exception.GetType().Name);
            return new PublishReceipt(JobPublishStatus.Unavailable, FailureCode: "jetstream-unavailable");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(
                "JetStream job publication failed. Failure type: {FailureType}",
                exception.GetType().Name);
            return new PublishReceipt(JobPublishStatus.Failed, FailureCode: "job-publication-failure");
        }
    }

    private static string GetSubject(JobType jobType) => jobType switch
    {
        JobType.Report => "swiftaid.jobs.report",
        JobType.CaseSummary => "swiftaid.jobs.case-summary",
        JobType.OpsContinuity => "swiftaid.jobs.ops-continuity",
        // Recognized by the envelope schema, but dispatch remains disabled until separately approved.
        JobType.Surveillance => throw new DisabledJobTypeException(),
        JobType.Knowledge => "swiftaid.jobs.knowledge",
        JobType.Reminder => "swiftaid.jobs.reminder",
        JobType.MemoryIndex => "swiftaid.jobs.memory-index",
        _ => throw new ArgumentOutOfRangeException(nameof(jobType), jobType, "The job type is unsupported.")
    };
}

internal interface IJobMessageTransport
{
    ValueTask<JobMessageAcknowledgement> PublishAsync(
        string subject,
        byte[] payload,
        string expectedStream,
        string messageId,
        CancellationToken cancellationToken);
}

internal sealed record JobMessageAcknowledgement(bool IsAcknowledged, string? Stream, ulong Sequence);

internal sealed class JetStreamJobMessageTransport(INatsJSContext jetStream) : IJobMessageTransport
{
    public async ValueTask<JobMessageAcknowledgement> PublishAsync(
        string subject,
        byte[] payload,
        string expectedStream,
        string messageId,
        CancellationToken cancellationToken)
    {
        var acknowledgement = await jetStream.PublishAsync(
            subject,
            payload,
            opts: new NatsJSPubOpts
            {
                ExpectedStream = expectedStream,
                MsgId = messageId
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        acknowledgement.EnsureStored();
        return new JobMessageAcknowledgement(true, acknowledgement.Stream, acknowledgement.Seq);
    }
}

internal sealed class DisabledJobTypeException : ArgumentException
{
}
