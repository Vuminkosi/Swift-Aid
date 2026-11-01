using System.Text.Json;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using SwiftAid.Application.Abstractions;
using SwiftAid.Contracts.Events;

namespace SwiftAid.Infrastructure.Messaging;

public sealed class JetStreamIngestionNoticePublisher(INatsClient client) : IIngestionNoticePublisher
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly INatsJSContext _jetStream = client.CreateJetStreamContext();

    public async ValueTask<EventPublishResult> PublishAsync(IngestionNotice notice, CancellationToken cancellationToken)
    {
        if (!notice.IsValid()) return new(EventPublishStatus.Failed, FailureCode: "invalid-notice");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(notice, Json);
        if (bytes.Length > 4096) return new(EventPublishStatus.Failed, FailureCode: "invalid-notice");
        try
        {
            var ack = await _jetStream.PublishAsync($"{IngestionNotice.SubjectPrefix}.{notice.SourceKind}", bytes,
                opts: new NatsJSPubOpts { ExpectedStream = IngestionNotice.StreamName, MsgId = notice.MessageId.ToString("D") },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            ack.EnsureStored();
            return ack.Stream == IngestionNotice.StreamName
                ? new(EventPublishStatus.AcknowledgedByJetStream, ack.Stream, ack.Seq)
                : new(EventPublishStatus.Failed, FailureCode: "unexpected-stream");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(EventPublishStatus.Cancelled, FailureCode: "publish-timeout");
        }
        catch (NatsException)
        {
            return new(EventPublishStatus.Unavailable, FailureCode: "jetstream-unavailable");
        }
    }
}
