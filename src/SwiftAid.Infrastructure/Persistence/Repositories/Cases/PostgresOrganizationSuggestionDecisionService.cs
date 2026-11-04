using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>Records an authorized, immutable decision without changing ownership or queue membership.</summary>
public sealed class PostgresOrganizationSuggestionDecisionService(
    SwiftAidDbContext db, ICaseCommandUnitOfWork unitOfWork, ICaseAccessPolicy policy, IDataProtectionProvider protection)
    : IOrganizationSuggestionDecisionService
{
    public async Task<OrganizationDecisionOutcome> DecideAsync(ActorContext actor, Guid caseId, Guid suggestionId,
        Guid commandId, OrganizationDecision decision, string rationale, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (caseId == Guid.Empty || suggestionId == Guid.Empty || commandId == Guid.Empty || !Enum.IsDefined(decision) ||
            string.IsNullOrWhiteSpace(rationale) || rationale.Length > 500 || rationale != rationale.Trim())
            return OrganizationDecisionOutcome.Invalid;
        ICaseCommandLease lease;
        try { lease = await unitOfWork.BeginCaseAsync(actor, caseId, [], CaseLeaseMode.Write, ct).ConfigureAwait(false); }
        catch (KeyNotFoundException) { return OrganizationDecisionOutcome.NotFoundOrNotAuthorized; }
        catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.")
        { return OrganizationDecisionOutcome.NotFoundOrNotAuthorized; }
        await using (lease.ConfigureAwait(false))
        {
            var authority = await policy.AuthorizeAllAsync(actor, caseId, [CaseOperation.ReadCase, CaseOperation.AssignCase], ct).ConfigureAwait(false);
            if (!authority.IsAllowed) return OrganizationDecisionOutcome.NotFoundOrNotAuthorized;
            var commandLock = $"ellai-organization-decision:{commandId:D}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({commandLock}, 0))", ct).ConfigureAwait(false);
            var previous = await db.Database.SqlQuery<DecisionRow>($"""
                SELECT suggestion_id AS "SuggestionId", case_id AS "CaseId", actor_id AS "ActorId",
                    decision AS "Decision", protected_rationale AS "ProtectedRationale"
                FROM ellai_organization_decisions WHERE command_id = {commandId}
                """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (previous is not null && (previous.SuggestionId != suggestionId || previous.CaseId != caseId ||
                previous.ActorId != actor.ActorId || previous.Decision != decision.ToString())) return OrganizationDecisionOutcome.Conflict;
            var current = await db.Database.SqlQuery<CurrentRow>($"""
                SELECT (c.aggregate_version = p.source_case_version AND c.workflow <> 'Resolved') AS "IsCurrent",
                    EXISTS (SELECT 1 FROM ellai_organization_decisions d WHERE d.suggestion_id = p.activity_id) AS "IsDecided"
                FROM ellai_organization_proposals p JOIN managed_cases c ON c.case_id = p.case_id
                WHERE p.activity_id = {suggestionId} AND p.case_id = {caseId}
                """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (current is null) return OrganizationDecisionOutcome.NotFoundOrNotAuthorized;
            var protector = protection.CreateProtector("SwiftAid.Ellai.OrganizationDecision.v1", caseId.ToString("D"), suggestionId.ToString("D"));
            if (previous is not null)
                return System.Text.Encoding.UTF8.GetString(protector.Unprotect(previous.ProtectedRationale)) == rationale ? OrganizationDecisionOutcome.Replayed : OrganizationDecisionOutcome.Conflict;
            if (current.IsDecided) return OrganizationDecisionOutcome.AlreadyDecided;
            if (!current.IsCurrent) return OrganizationDecisionOutcome.Stale;
            var protectedRationale = protector.Protect(System.Text.Encoding.UTF8.GetBytes(rationale));
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ellai_organization_decisions (suggestion_id, case_id, command_id, actor_id, decision, protected_rationale, recorded_at_utc)
                VALUES ({suggestionId}, {caseId}, {commandId}, {actor.ActorId}, {decision.ToString()}, {protectedRationale}, clock_timestamp())
                ON CONFLICT DO NOTHING
                """, ct).ConfigureAwait(false);
            if (inserted == 0) return OrganizationDecisionOutcome.Conflict;
            await lease.CommitAsync(ct).ConfigureAwait(false);
            return OrganizationDecisionOutcome.Recorded;
        }
    }

    private sealed class DecisionRow
    {
        public Guid SuggestionId { get; init; }
        public Guid CaseId { get; init; }
        public string ActorId { get; init; } = "";
        public string Decision { get; init; } = "";
        public byte[] ProtectedRationale { get; init; } = [];
    }

    private sealed class CurrentRow
    {
        public bool IsCurrent { get; init; }
        public bool IsDecided { get; init; }
    }
}


