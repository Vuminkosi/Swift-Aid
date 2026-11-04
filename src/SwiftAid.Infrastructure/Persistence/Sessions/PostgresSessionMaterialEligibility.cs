using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Sessions;

namespace SwiftAid.Infrastructure.Persistence.Sessions;

/// <summary>Checks current organization approval for every exact material version.</summary>
public sealed class PostgresSessionMaterialEligibility(SwiftAidDbContext dbContext) : ISessionMaterialEligibility
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<bool> AreEligibleAsync(SessionMaterialEligibilityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.OrganizationId) || request.Materials.Count == 0) return false;
        foreach (var material in request.Materials)
        {
            var eligible = await _dbContext.SessionMaterialApprovals.AsNoTracking().AnyAsync(approval =>
                approval.OrganizationId == request.OrganizationId && approval.MaterialId == material.MaterialId &&
                approval.SourceVersion == material.SourceVersion && approval.RevokedAtUtc == null &&
                approval.RightsEvidence == material.RightsEvidence && approval.Version > 0,
                cancellationToken).ConfigureAwait(false);
            if (!eligible) return false;
        }
        return true;
    }
}
