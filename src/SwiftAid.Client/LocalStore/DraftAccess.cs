namespace SwiftAid.Client.LocalStore;

/// <summary>Trusted, refreshed offline account/workspace snapshot supplied by the native bootstrap.</summary>
public sealed record LocalDraftAccessContext(
    string ActorId,
    bool IsProvisioned,
    bool IsActive,
    long AccessEpoch,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyCollection<string> PermittedWorkspaceIds);

/// <summary>
/// Supplies a context established by the authenticated native bootstrap. Implementations must not
/// construct this value from caller-provided draft fields or treat local storage as authority.
/// </summary>
public interface ILocalDraftAccessContextProvider
{
    ValueTask<LocalDraftAccessContext?> GetCurrentAsync(CancellationToken cancellationToken);
}

/// <summary>Platform-protected per-account key storage. Implementations must fail closed.</summary>
public interface ILocalDraftKeyStore
{
    ValueTask<byte[]> GetOrCreateKeyAsync(string actorId, CancellationToken cancellationToken);
}

public sealed class LocalDraftAccessDeniedException : Exception
{
    public LocalDraftAccessDeniedException() : base("A current provisioned account and permitted workspace are required for local staff drafts.") { }
}

public sealed class LocalDraftKeyUnavailableException : Exception
{
    public LocalDraftKeyUnavailableException() : base("The protected local draft key is unavailable.") { }
}

public sealed class LocalDraftIntegrityException : Exception
{
    public LocalDraftIntegrityException() : base("The saved local draft failed integrity validation.") { }
}
