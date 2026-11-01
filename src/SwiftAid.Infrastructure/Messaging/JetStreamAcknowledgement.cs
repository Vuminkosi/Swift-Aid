using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace SwiftAid.Infrastructure.Messaging;

internal static class JetStreamAcknowledgement
{
    /// <summary>A duplicate PubAck confirms earlier storage. The caller must also verify the expected stream.</summary>
    public static void EnsureStored(this PubAckResponse acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (acknowledgement.Error is not null) throw new NatsJSApiException(acknowledgement.Error);
        if (acknowledgement.Seq == 0 || string.IsNullOrWhiteSpace(acknowledgement.Stream))
            throw new NatsException("JetStream did not acknowledge a stored message.");
    }
}
