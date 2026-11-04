namespace SwiftAid.Client.Presentation;

/// <summary>Explicit display context; selecting an organization never grants authority.</summary>
public sealed class OrganizationManagementContext
{
    public string? OrganizationId { get; set; }
}