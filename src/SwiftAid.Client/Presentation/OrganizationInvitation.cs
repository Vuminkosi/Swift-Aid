namespace SwiftAid.Client.Presentation;

/// <summary>Invitation record shared by organization-management operations.</summary>
public sealed record OrganizationInvitation(
    string Id,
    string Email,
    string Role,
    string Status,
    bool IsSynthetic);
