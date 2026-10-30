using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Registry query: goal-relevant bounded slice, never the whole app.</summary>
public sealed record UiRegistryQuery(
    string FeatureId,
    IReadOnlyList<string> GoalTags,
    int MaxDescriptors);

/// <summary>Permission-filtered registry snapshot for one request scope.</summary>
public sealed record UiRegistrySnapshot(
    IReadOnlyList<UiControlDescriptor> Descriptors,
    string CatalogueVersion,
    string UiContractVersion,
    bool Truncated);

/// <summary>Versioned semantic UI registry (ELL-T33 groundwork).
/// Definitions are explicit application metadata registered once near the
/// owning feature; the model receives only an eligible bounded projection.
/// Unregistered controls are not agent-controllable.</summary>
public sealed class EllaiUiRegistry
{
    public const string CurrentCatalogueVersion = "ellai-catalogue/1.0";

    public const string CurrentUiContractVersion = "ellai-uicontract/1.0";

    private readonly Dictionary<string, UiControlDescriptor> _descriptors = new(StringComparer.Ordinal);

    public void Register(UiControlDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.ControlId) || string.IsNullOrWhiteSpace(descriptor.FeatureId))
        {
            throw new ArgumentException("Descriptor requires control and feature IDs.");
        }

        _descriptors[descriptor.ControlId] = descriptor;
    }

    /// <summary>Returns the goal-relevant, permission-filtered projection:
    /// only descriptors the scope is entitled to, bounded, without resolved
    /// URLs/selectors (those stay in trusted adapters).</summary>
    public UiRegistrySnapshot GetSnapshot(
        UiRegistryQuery query, IReadOnlySet<string> entitledFeatures, IReadOnlySet<string> entitledCapabilities)
    {
        ArgumentNullException.ThrowIfNull(query);

        var matches = _descriptors.Values
            .Where(d => string.Equals(d.FeatureId, query.FeatureId, StringComparison.Ordinal))
            .Where(d => entitledFeatures.Contains(d.FeatureId))
            .Where(d => d.RequiredCapabilities.All(entitledCapabilities.Contains))
            .Where(d => query.GoalTags.Count == 0 || d.GoalTags.Any(query.GoalTags.Contains))
            .OrderBy(d => d.ControlId, StringComparer.Ordinal)
            .ToList();

        var bound = Math.Min(query.MaxDescriptors, 20);
        var truncated = matches.Count > bound;
        return new UiRegistrySnapshot(
            matches.Take(bound).ToList(),
            CurrentCatalogueVersion, CurrentUiContractVersion, truncated);
    }
}
