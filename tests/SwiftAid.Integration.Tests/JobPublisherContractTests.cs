using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Jobs;
using SwiftAid.Contracts.Jobs;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Messaging;

namespace SwiftAid.Integration.Tests;

public sealed class JobPublisherContractTests
{
    [Fact]
    public async Task PublishUsesExplicitSubjectExpectedStreamAndMessageIdAndReturnsMinimizedReceipt()
    {
        var transport = new FakeJobMessageTransport
        {
            Acknowledgement = new JobMessageAcknowledgement(true, JobPublisherOptions.ExpectedStreamName, 41)
        };
        var publisher = CreatePublisher(transport);
        var envelope = CreateEnvelope();

        var receipt = await publisher.PublishAsync(envelope, CancellationToken.None);

        Assert.Equal(JobPublishStatus.AcknowledgedByJetStream, receipt.Status);
        Assert.Equal(JobPublisherOptions.ExpectedStreamName, receipt.StreamName);
        Assert.Equal((ulong)41, receipt.StreamSequence);
        Assert.Null(receipt.FailureCode);
        Assert.Equal("swiftaid.jobs.case-summary", transport.Subject);
        Assert.Equal(JobPublisherOptions.ExpectedStreamName, transport.ExpectedStream);
        Assert.Equal(envelope.MessageId.ToString("D"), transport.MessageId);
        Assert.Equal(envelope, JobEnvelope.DeserializeFromUtf8(transport.Payload!));
    }

    [Fact]
    public async Task PublishRejectsUnknownJobTypeWithoutCallingTransport()
    {
        var transport = new FakeJobMessageTransport();
        var publisher = CreatePublisher(transport);

        var receipt = await publisher.PublishAsync(CreateEnvelope() with { JobType = (JobType)999 }, CancellationToken.None);

        Assert.Equal(JobPublishStatus.Failed, receipt.Status);
        Assert.Equal("invalid-job-envelope", receipt.FailureCode);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task PublishRejectsSchemaRecognizedButDisabledSurveillanceDispatch()
    {
        var transport = new FakeJobMessageTransport();
        var publisher = CreatePublisher(transport);

        var receipt = await publisher.PublishAsync(CreateEnvelope() with { JobType = JobType.Surveillance }, CancellationToken.None);

        Assert.Equal(JobPublishStatus.Failed, receipt.Status);
        Assert.Equal("job-type-disabled", receipt.FailureCode);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task PublishRequiresAJetStreamAcknowledgement()
    {
        var transport = new FakeJobMessageTransport
        {
            Acknowledgement = new JobMessageAcknowledgement(false, JobPublisherOptions.ExpectedStreamName, 0)
        };
        var publisher = CreatePublisher(transport);

        var receipt = await publisher.PublishAsync(CreateEnvelope(), CancellationToken.None);

        Assert.Equal(JobPublishStatus.Failed, receipt.Status);
        Assert.Equal("jetstream-not-acknowledged", receipt.FailureCode);
    }

    [Fact]
    public async Task PublishRejectsAcknowledgementFromUnexpectedStream()
    {
        var transport = new FakeJobMessageTransport
        {
            Acknowledgement = new JobMessageAcknowledgement(true, "OTHER_STREAM", 41)
        };
        var publisher = CreatePublisher(transport);

        var receipt = await publisher.PublishAsync(CreateEnvelope(), CancellationToken.None);

        Assert.Equal(JobPublishStatus.Failed, receipt.Status);
        Assert.Equal("unexpected-stream-ack", receipt.FailureCode);
        Assert.Null(receipt.StreamName);
        Assert.Null(receipt.StreamSequence);
    }

    [Fact]
    public async Task PublishEnforcesConfiguredPayloadLimitBeforeCallingTransport()
    {
        var transport = new FakeJobMessageTransport();
        var publisher = CreatePublisher(transport, maximumMessageBytes: 1);

        var receipt = await publisher.PublishAsync(CreateEnvelope(), CancellationToken.None);

        Assert.Equal(JobPublishStatus.Failed, receipt.Status);
        Assert.Equal("job-message-too-large", receipt.FailureCode);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task PublishReturnsCancelledWithoutCallingTransportWhenAlreadyCancelled()
    {
        var transport = new FakeJobMessageTransport();
        var publisher = CreatePublisher(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var receipt = await publisher.PublishAsync(CreateEnvelope(), cancellation.Token);

        Assert.Equal(JobPublishStatus.Cancelled, receipt.Status);
        Assert.Equal(0, transport.CallCount);
    }

    private static JetStreamPublisher CreatePublisher(
        FakeJobMessageTransport transport,
        int maximumMessageBytes = JobEnvelope.MaximumMessageBytes) => new(
            transport,
            Options.Create(new JobPublisherOptions { MaximumMessageBytes = maximumMessageBytes }),
            NullLogger<JetStreamPublisher>.Instance);

    private static JobEnvelope CreateEnvelope() => new(
        JobEnvelope.CurrentSchemaVersion,
        Guid.Parse("9160e07e-1385-4b30-995b-eae5fd2651db"),
        Guid.Parse("a8671879-8132-4f46-a689-628fe3791d57"),
        JobType.CaseSummary,
        "demo-zw",
        Guid.Parse("d31a5e8b-0445-4134-894c-57519ce031b9"),
        Guid.Parse("081a93cf-1353-43b7-ae5d-dd045786a527"),
        new DateTimeOffset(2026, 10, 25, 7, 0, 0, TimeSpan.Zero));

    private sealed class FakeJobMessageTransport : IJobMessageTransport
    {
        public JobMessageAcknowledgement Acknowledgement { get; init; } = new(true, JobPublisherOptions.ExpectedStreamName, 1);

        public int CallCount { get; private set; }

        public string? Subject { get; private set; }

        public byte[]? Payload { get; private set; }

        public string? ExpectedStream { get; private set; }

        public string? MessageId { get; private set; }

        public ValueTask<JobMessageAcknowledgement> PublishAsync(
            string subject,
            byte[] payload,
            string expectedStream,
            string messageId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Subject = subject;
            Payload = payload;
            ExpectedStream = expectedStream;
            MessageId = messageId;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Acknowledgement);
        }
    }
}
