namespace SwiftAid.Application.Cases.Commands;

/// <summary>Bounds whole-transaction retries for explicitly retryable persistence races.</summary>
public sealed class CaseCommandProcessingOptions
{
    public int MaximumTransactionAttempts { get; init; } = 3;

    public void Validate()
    {
        if (MaximumTransactionAttempts is < 1 or > 3)
        {
            throw new InvalidOperationException("Maximum transaction attempts must be between one and three.");
        }
    }
}
