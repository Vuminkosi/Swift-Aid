namespace SwiftAid.Application.Authorization;

/// <summary>
/// Reads current grants from an authoritative source for each sensitive request.
/// Implementations must not rely only on signed claims or cached role labels.
/// </summary>
public interface ICurrentAccessGrantStore
{
    ValueTask<CurrentAccessGrant?> GetCurrentGrantAsync(
        ActorContext actor,
        CancellationToken cancellationToken);
}

public enum AccessGrantStatus
{
    Active,
    Suspended,
    Revoked
}

/// <summary>A point-in-time actor grant snapshot returned by the trusted grant store.</summary>
public sealed class CurrentAccessGrant
{
    public CurrentAccessGrant(
        string actorId,
        long accessEpoch,
        DateTimeOffset expiresAtUtc,
        AccessGrantStatus status,
        IEnumerable<ScopedAccessGrant> grants)
    {
        ActorId = RequireIdentifier(actorId, nameof(actorId));
        if (accessEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(accessEpoch));
        }

        if (expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Grant expiry must use UTC.", nameof(expiresAtUtc));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentNullException.ThrowIfNull(grants);
        var snapshot = grants.ToArray();
        if (snapshot.Any(grant => grant is null))
        {
            throw new ArgumentException("Grant groups cannot contain null entries.", nameof(grants));
        }

        if (snapshot.Select(grant => grant.GroupId).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException("Grant group identifiers must be unique.", nameof(grants));
        }

        AccessEpoch = accessEpoch;
        ExpiresAtUtc = expiresAtUtc;
        Status = status;
        Grants = Array.AsReadOnly(snapshot);
    }

    public string ActorId { get; }

    public long AccessEpoch { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public AccessGrantStatus Status { get; }

    public IReadOnlyList<ScopedAccessGrant> Grants { get; }

    private static string RequireIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A non-empty canonical identifier is required.", parameterName);
        }

        return value;
    }
}
