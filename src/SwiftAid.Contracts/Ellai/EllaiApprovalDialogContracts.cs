namespace SwiftAid.Contracts.Ellai;

/// <summary>One human-reviewable action row: stable id plus safe display
/// label and detail. Rendered by the approval dialog; the server rechecks
/// the exact plan binding before any effect.</summary>
public sealed record EllaiApprovalActionDescriptor(
    string ActionId,
    string Label,
    string Detail);
