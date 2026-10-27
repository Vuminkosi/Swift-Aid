using SwiftAid.Contracts.Jobs;

namespace SwiftAid.Infrastructure.Configuration;

public sealed class JobPublisherOptions
{
    public const string SectionName = "Nats:Jobs";

    public const string ExpectedStreamName = "SWIFTAID_JOBS";

    public string StreamName { get; set; } = ExpectedStreamName;

    public int MaximumMessageBytes { get; set; } = JobEnvelope.MaximumMessageBytes;

    public void Validate()
    {
        if (!string.Equals(StreamName, ExpectedStreamName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The job publisher must target the configured SWIFTAID_JOBS stream.", nameof(StreamName));
        }

        if (MaximumMessageBytes is < 1 or > JobEnvelope.MaximumMessageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumMessageBytes),
                "The job message limit must be positive and no greater than 64 KiB.");
        }
    }
}
