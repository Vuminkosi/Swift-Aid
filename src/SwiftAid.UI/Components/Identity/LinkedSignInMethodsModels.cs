namespace SwiftAid.UI.Components.Identity;

public enum LinkedIdentityViewState
{
    Loading,
    Unavailable,
    SignedOut,
    Ready
}

public sealed record LinkedIdentityRow(
    string Id,
    string DisplayName,
    bool CurrentlyAvailable);

public sealed record LinkableProviderRow(
    string Id,
    string DisplayName);
