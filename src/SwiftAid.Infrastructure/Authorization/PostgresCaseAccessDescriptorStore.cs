using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Infrastructure.Authorization;

/// <summary>Reads the locked case projection and geographic scope from PostgreSQL.</summary>
public sealed class PostgresCaseAccessDescriptorStore : ICaseAccessDescriptorStore
{
    private readonly SwiftAidDbContext _dbContext;
    private readonly ICaseCommandLeaseAccessor _leaseAccessor;

    public PostgresCaseAccessDescriptorStore(
        SwiftAidDbContext dbContext,
        ICaseCommandLeaseAccessor leaseAccessor)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _leaseAccessor = leaseAccessor ?? throw new ArgumentNullException(nameof(leaseAccessor));
    }

    public async ValueTask<CaseAccessDescriptor?> GetCurrentDescriptorAsync(
        Guid caseId,
        CancellationToken cancellationToken)
    {
        _ = PostgresLeaseGuard.RequireCurrent(_dbContext, _leaseAccessor, caseId);

        var resource = await _dbContext.ManagedCases
            .AsNoTracking()
            .Where(entity => entity.CaseId == caseId)
            .Select(entity => new
            {
                entity.CaseId,
                entity.ResourceVersion,
                entity.OrganizationId,
                entity.WorkspaceId,
                entity.CountryCode,
                entity.Sector,
                entity.SensitivityCode
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (resource is null)
        {
            return null;
        }

        if (resource.CaseId != caseId)
        {
            throw new InvalidOperationException("The locked case projection did not match the requested case.");
        }

        var regionIds = await _dbContext.ManagedCaseRegions
            .AsNoTracking()
            .Where(region => region.CaseId == caseId)
            .OrderBy(region => region.RegionId)
            .Select(region => region.RegionId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        return new CaseAccessDescriptor(
            resource.CaseId,
            resource.ResourceVersion,
            resource.OrganizationId,
            resource.WorkspaceId,
            resource.CountryCode,
            regionIds,
            resource.Sector,
            resource.SensitivityCode);
    }
}
