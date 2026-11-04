namespace SwiftAid.Intelligence.Meta;

/// <summary>
/// A source-backed provider entry. Registrations are trusted server configuration, never caller supplied
/// endpoint data. A capability listing is not proof that an account has access to that capability.
/// </summary>
public sealed record MetaEndpointRegistration(
    InferenceRouteKind Route,
    string RouteAlias,
    string ModelId,
    Uri Endpoint,
    string ExpectedAuthority,
    string EvidenceReference,
    bool PublicAuthorityVerified,
    string? AsrModelId,
    bool ToolsListed,
    IReadOnlyCollection<string> AudioFormatsListed,
    IReadOnlyCollection<string> LanguagesListed);

/// <summary>
/// Resolves an exact source-reviewed route alias and model pair. Implementations must return null unless the
/// pair and endpoint authority are recorded in the approved source registry. No default registry is provided.
/// </summary>
public interface IMetaEndpointRegistry
{
    ValueTask<MetaEndpointRegistration?> FindAsync(
        string routeAlias,
        string modelId,
        CancellationToken cancellationToken);
}

/// <summary>Safe default while no endpoint/model pair has a verified source entry.</summary>
public sealed class EmptyMetaEndpointRegistry : IMetaEndpointRegistry
{
    public static EmptyMetaEndpointRegistry Instance { get; } = new();

    private EmptyMetaEndpointRegistry()
    {
    }

    public ValueTask<MetaEndpointRegistration?> FindAsync(
        string routeAlias,
        string modelId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<MetaEndpointRegistration?>(null);
    }
}
