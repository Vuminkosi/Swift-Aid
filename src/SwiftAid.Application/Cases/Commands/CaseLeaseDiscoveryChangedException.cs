using System.Collections.Immutable;

namespace SwiftAid.Application.Cases.Commands;

/// <summary>
/// Indicates that non-authoritative lock hints changed before the case row was locked.
/// The caller must dispose the lease and restart with the returned actor hints.
/// </summary>
public sealed class CaseLeaseDiscoveryChangedException : InvalidOperationException
{
    public CaseLeaseDiscoveryChangedException(Guid caseId, IEnumerable<string> additionalActorHints)
        : base("Case authority changed during lock discovery; restart the transaction with the supplied hints.")
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("A case identifier is required.", nameof(caseId));
        }

        ArgumentNullException.ThrowIfNull(additionalActorHints);
        var hints = additionalActorHints
            .Select(hint => string.IsNullOrWhiteSpace(hint) || !string.Equals(hint, hint.Trim(), StringComparison.Ordinal)
                ? throw new ArgumentException("Actor hints must be non-empty canonical identifiers.", nameof(additionalActorHints))
                : hint)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

        CaseId = caseId;
        AdditionalActorHints = hints;
    }

    public Guid CaseId { get; }

    public ImmutableArray<string> AdditionalActorHints { get; }
}
