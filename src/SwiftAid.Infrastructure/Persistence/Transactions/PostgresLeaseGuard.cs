using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Authorization;

namespace SwiftAid.Infrastructure.Persistence.Transactions;

internal static class PostgresLeaseGuard
{
    public static PostgresCaseCommandLease RequireCurrent(
        SwiftAidDbContext dbContext,
        ICaseCommandLeaseAccessor leaseAccessor,
        Guid caseId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(leaseAccessor);
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("A case identifier is required.", nameof(caseId));
        }

        if (leaseAccessor.CurrentLease is not PostgresCaseCommandLease lease ||
            !lease.IsActive ||
            lease.CaseId != caseId ||
            !ReferenceEquals(lease.DbContext, dbContext) ||
            !ReferenceEquals(dbContext.Database.CurrentTransaction, lease.Transaction))
        {
            throw new InvalidOperationException("An active lease for this case and scoped PostgreSQL context is required.");
        }

        return lease;
    }

    public static PostgresCaseCommandLease RequireCurrent(
        SwiftAidDbContext dbContext,
        ICaseCommandLeaseAccessor leaseAccessor)
    {
        ArgumentNullException.ThrowIfNull(leaseAccessor);
        var lease = leaseAccessor.CurrentLease;
        if (lease is null)
        {
            throw new InvalidOperationException("An active case lease is required.");
        }

        return RequireCurrent(dbContext, leaseAccessor, lease.CaseId);
    }

    public static PostgresCaseCommandLease RequireCurrent(
        SwiftAidDbContext dbContext,
        ICaseCommandLeaseAccessor leaseAccessor,
        ActorContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var lease = RequireCurrent(dbContext, leaseAccessor);
        if (!string.Equals(lease.PrimaryActorId, actor.ActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The active case lease belongs to a different authenticated actor.");
        }

        return lease;
    }
}
