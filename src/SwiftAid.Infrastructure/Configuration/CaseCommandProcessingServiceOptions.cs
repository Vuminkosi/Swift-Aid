namespace SwiftAid.Infrastructure.Configuration;

/// <summary>Controls whether the managed-case command persistence graph is available.</summary>
public sealed class CaseCommandProcessingServiceOptions
{
    public const string SectionName = "CaseCommandProcessing";

    public bool Enabled { get; init; }

    public int MaximumTransactionAttempts { get; init; } = 3;

    public void Validate()
    {
        if (MaximumTransactionAttempts is < 1 or > 3)
        {
            throw new InvalidOperationException(
                "CaseCommandProcessing:MaximumTransactionAttempts must be between one and three.");
        }
    }
}
