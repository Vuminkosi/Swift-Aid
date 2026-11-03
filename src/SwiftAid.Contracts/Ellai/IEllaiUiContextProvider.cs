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
    IReadOnlyList<string> CompletionSignals)
{
    public IReadOnlyList<EllaiUiInputDescriptor> Inputs { get; init; } = [];
}

/// <summary>Static input inventory for planning. Contains no entered values or completion claims.</summary>
public sealed record EllaiUiInputDescriptor(string InputId, string Label, string InputType,
    bool Required, string ApplicableWhen, string Validation);

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
    IReadOnlyList<UiControlState> Controls)
{
    /// <summary>Application-authored validation guidance; contains no entered field values.</summary>
    public IReadOnlyList<EllaiUiValidationIssue> ValidationIssues { get; init; } = [];
}

public sealed record EllaiUiValidationIssue(string InputId, string Anchor, string Message);
