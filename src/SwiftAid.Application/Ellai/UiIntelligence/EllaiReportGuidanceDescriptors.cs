using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Registers the same manifest used by the report component. No separate page knowledge base.</summary>
public static class EllaiReportGuidanceDescriptors
{
    public const string FeatureId = EllaiReportUiManifest.FeatureId;
    public const string NavigationId = EllaiReportUiManifest.NavigationId;
    public static IReadOnlyList<UiControlDescriptor> All => EllaiReportUiManifest.All;

    public static void RegisterAll(EllaiUiRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach (var descriptor in All)
        {
            registry.Register(descriptor);
        }
    }
}
