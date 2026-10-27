namespace SwiftAid.Application.Cases.Commands;

/// <summary>A sanitized persistence race that may require restarting the complete lease.</summary>
public sealed class CasePersistenceRaceException : Exception
{
    public CasePersistenceRaceException(CasePersistenceRaceKind kind)
        : base("The case command transaction encountered a retryable persistence race.")
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
    }

    public CasePersistenceRaceKind Kind { get; }
}

public enum CasePersistenceRaceKind
{
    SerializationFailure,
    Deadlock,
    ReceiptKeyConflict,
    AuthorityDiscoveryChanged
}
