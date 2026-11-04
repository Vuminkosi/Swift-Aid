namespace SwiftAid.Application.Identity;

/// <summary>Checks current owner authority for an organization. Membership alone never grants ownership.</summary>
public interface IOrganizationOwnerAuthorizer
{
    Task<bool> IsActiveOwnerAsync(string actorId, string organizationId, CancellationToken cancellationToken);
}
