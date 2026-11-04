using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using SwiftAid.Infrastructure.Messaging;

namespace SwiftAid.Integration.Tests;

public sealed class JetStreamAcknowledgementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stored_and_duplicate_acknowledgements_both_confirm_storage(bool duplicate) =>
        new PubAckResponse { Stream = "SWIFTAID_INGESTION", Seq = 12, Duplicate = duplicate }.EnsureStored();

    [Fact]
    public void An_error_is_not_accepted_even_if_duplicate_is_set() =>
        Assert.Throws<NatsJSApiException>(() => new PubAckResponse
        { Stream = "SWIFTAID_INGESTION", Seq = 12, Duplicate = true, Error = new ApiError { Code = 500 } }.EnsureStored());

    [Fact]
    public void Missing_sequence_is_not_storage() =>
        Assert.Throws<NatsException>(() => new PubAckResponse { Stream = "SWIFTAID_INGESTION", Seq = 0 }.EnsureStored());
}
