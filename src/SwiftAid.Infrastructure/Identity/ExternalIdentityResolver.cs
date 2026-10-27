using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Infrastructure.Identity;

/// <summary>
/// Identity claims extracted only after a configured authentication handler validates its response.
/// Email and provider profile data are intentionally excluded from the identity key.
/// </summary>
public sealed record ValidatedExternalIdentity(string Provider, string Issuer, string Subject);

public enum ExternalIdentityResolutionStatus
{
    Resolved,
    Unavailable,
    Rejected
}

public sealed record ExternalIdentityResolution(
    ExternalIdentityResolutionStatus Status,
    string? ActorId = null,
    bool Created = false);

public interface IExternalIdentityResolver
{
    ValueTask<ExternalIdentityResolution> ResolveAsync(
        ValidatedExternalIdentity identity,
        CancellationToken cancellationToken);
}

public interface IExternalIdentityMappingStore
{
    ValueTask<ExternalIdentityResolution> ResolveOrCreateOrdinaryActorAsync(
        string provider,
        string issuer,
        string subject,
        CancellationToken cancellationToken);

    ValueTask<ExternalIdentityResolution> ResolveExistingAsync(
        string provider,
        string issuer,
        string subject,
        CancellationToken cancellationToken);

    ValueTask<ExternalIdentityLinkStatus> LinkAsync(
        string actorId,
        string provider,
        string issuer,
        string subject,
        CancellationToken cancellationToken);

    ValueTask<ExternalIdentityUnlinkStatus> UnlinkProviderAsync(
        string actorId,
        string provider,
        CancellationToken cancellationToken);
}

public enum ExternalIdentityLinkStatus
{
    Linked,
    AlreadyLinked,
    Conflict,
    ActorUnavailable,
    Rejected
}

public enum ExternalIdentityUnlinkStatus
{
    Unlinked,
    NotLinked,
    LastLoginRoute,
    ActorUnavailable,
    Rejected
}

/// <summary>Applies the fixed provider allowlist before touching the identity store.</summary>
public sealed class ExternalIdentityResolver(
    FederationOptions options,
    OrganizationSsoRegistry organizationSsoRegistry,
    IExternalIdentityMappingStore mappingStore) : IExternalIdentityResolver
{
    public ValueTask<ExternalIdentityResolution> ResolveAsync(
        ValidatedExternalIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!options.Enabled ||
            string.IsNullOrWhiteSpace(identity.Provider) ||
            string.IsNullOrWhiteSpace(identity.Issuer) ||
            string.IsNullOrWhiteSpace(identity.Subject))
        {
            return ValueTask.FromResult(new ExternalIdentityResolution(ExternalIdentityResolutionStatus.Rejected));
        }

        if (!TryGetExpectedIssuer(identity.Provider, out var expectedIssuer) ||
            !string.Equals(identity.Issuer, expectedIssuer, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(new ExternalIdentityResolution(ExternalIdentityResolutionStatus.Rejected));
        }

        return mappingStore.ResolveOrCreateOrdinaryActorAsync(
            identity.Provider,
            expectedIssuer,
            identity.Subject,
            cancellationToken);
    }

    private bool TryGetExpectedIssuer(string provider, out string issuer)
    {
        if (string.Equals(provider, FederationSetup.GoogleProviderId, StringComparison.Ordinal) &&
            options.Google.Enabled && options.Google.CallbackVerified)
        {
            issuer = FederationSetup.GoogleIssuer;
            return true;
        }

        if (string.Equals(provider, FederationSetup.FacebookProviderId, StringComparison.Ordinal) &&
            options.Facebook.Enabled && options.Facebook.CallbackVerified)
        {
            issuer = FederationSetup.FacebookIssuer;
            return true;
        }

        if (organizationSsoRegistry.TryGet(provider, out var organizationProvider) &&
            organizationProvider is not null)
        {
            issuer = organizationProvider.Issuer;
            return true;
        }

        issuer = string.Empty;
        return false;
    }
}
