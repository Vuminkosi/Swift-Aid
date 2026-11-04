using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Identity;
using SwiftAid.Application.Sessions;

namespace SwiftAid.Infrastructure.Persistence.Sessions;

/// <summary>Allows only a current organization owner to withdraw a material-version approval.</summary>
public sealed class PostgresSessionMaterialApprovalManager(
    SwiftAidDbContext dbContext,
    IOrganizationOwnerAuthorizer ownerAuthorizer,
    TimeProvider timeProvider) : ISessionMaterialApprovalManager
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IOrganizationOwnerAuthorizer _ownerAuthorizer = ownerAuthorizer ?? throw new ArgumentNullException(nameof(ownerAuthorizer));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<bool> RevokeAsync(string actorId, string organizationId, string materialId, string sourceVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(organizationId) ||
            string.IsNullOrWhiteSpace(materialId) || string.IsNullOrWhiteSpace(sourceVersion))
            throw new ArgumentException("A valid organization and material version are required.");
        if (organizationId.Length > 200 || materialId.Length > 256 || sourceVersion.Length > 256)
            throw new ArgumentException("The organization or material version identifier is too long.");
        if (!await _ownerAuthorizer.IsActiveOwnerAsync(actorId, organizationId, cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException();
        var approval = await _dbContext.SessionMaterialApprovals.SingleOrDefaultAsync(item =>
            item.OrganizationId == organizationId && item.MaterialId == materialId && item.SourceVersion == sourceVersion,
            cancellationToken).ConfigureAwait(false);
        if (approval is null || approval.RevokedAtUtc is not null) return false;
        approval.RevokedByActorId = actorId;
        approval.RevokedAtUtc = _timeProvider.GetUtcNow();
        approval.Version++;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
