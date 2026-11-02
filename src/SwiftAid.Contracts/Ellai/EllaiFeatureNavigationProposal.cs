namespace SwiftAid.Contracts.Ellai;

/// <summary>Only registered application destinations or bounded local back navigation.</summary>
public static class EllaiFeatureNavigationProposal
{
    public static bool IsBack(UiIntentProposal? proposal) => proposal is
        { Kind: UiIntentKind.NavigateFeature, FeatureId: "navigation", ResourceId: null, ControlId: null } &&
        proposal.Arguments is { Count: 1 } && proposal.Arguments.GetValueOrDefault("command") == "back";
    public static EllaiNavigationDestination? Destination(UiIntentProposal? proposal) => proposal is
        { Kind: UiIntentKind.NavigateFeature, FeatureId: "navigation", ResourceId: null, Arguments.Count: 0 }
            ? EllaiNavigationManifest.Destinations.SingleOrDefault(item => item.ControlId == proposal.ControlId) : null;
    public static bool IsValid(UiIntentProposal? proposal) => IsBack(proposal) || Destination(proposal) is not null;
}
