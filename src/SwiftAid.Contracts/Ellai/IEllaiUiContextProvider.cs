namespace SwiftAid.Contracts.Ellai;

/// <summary>Component-owned description and value-free presentation state.
/// Neither grants authority nor proves a persisted application action.</summary>
public interface IEllaiUiContextProvider
{
    string ComponentId { get; }
    EllaiUiManifest Describe();
    EllaiUiState GetState();
}

public sealed record EllaiUiSemantics(
    string Purpose,
    string? InputType,
    IReadOnlyList<string> ValidationRequirements,
    IReadOnlyList<string> CompletionSignals);

/// <summary>Static capabilities shared by the owning component and trusted server catalogue.</summary>
public sealed record EllaiUiManifest(
    string ComponentId,
    string NavigationId,
    string? ParentComponentId,
    string Purpose,
    IReadOnlyList<UiControlDescriptor> Controls);

/// <summary>Deliberately excludes form values, free text, URLs and arbitrary state dictionaries.</summary>
public sealed record EllaiUiState(
    string ComponentId,
    bool Loading,
    bool HasValidationErrors,
    IReadOnlyList<UiControlState> Controls);
