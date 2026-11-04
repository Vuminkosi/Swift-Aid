using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

public sealed class PostgresHealthOfficerEscalationReviewStore(SwiftAidDbContext db, ICaseCommandLeaseAccessor leases)
    : IHealthOfficerEscalationReviewStore
{
    public async Task<HealthOfficerEscalationReviewData?> GetAsync(ICaseCommandLease lease, Guid caseId, CancellationToken ct)
    {
        var current = PostgresLeaseGuard.RequireCurrent(db, leases, caseId);
        if (!ReferenceEquals(current, lease) || current.Mode != CaseLeaseMode.Read)
            throw new UnauthorizedAccessException("An active case read lease is required to review officer escalations.");
        var versions = await db.Database.SqlQuery<long>($"""
            SELECT aggregate_version AS "Value" FROM managed_cases WHERE case_id = {caseId} AND workflow <> 'Resolved'
            """).ToArrayAsync(ct).ConfigureAwait(false);
        if (versions.Length != 1) return null;
        var rows = await db.Database.SqlQuery<HealthOfficerEscalationReviewItem>($"""
            SELECT e.escalation_id AS "EscalationId", ev.recorded_at_utc AS "RaisedAtUtc"
            FROM managed_case_escalations e JOIN case_events ev ON ev.case_id = e.case_id
                AND ev.aggregate_version = e.raised_version AND ev.event_type = 'CaseEscalationRaised'
            WHERE e.case_id = {caseId} AND e.status = 'Raised'
                AND NOT EXISTS (SELECT 1 FROM health_officer_escalation_assignments a WHERE a.escalation_id = e.escalation_id)
            ORDER BY e.raised_version DESC, e.escalation_id LIMIT 21
            """).ToArrayAsync(ct).ConfigureAwait(false);
        return new(versions[0], rows.Take(20).ToArray(), rows.Length > 20);
    }
}
