using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Cases.Correlation;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>Records a reviewed candidate without merging cases or changing ownership.</summary>
public sealed class PostgresCaseCorrelationDecisionService(SwiftAidDbContext db, ICaseCommandUnitOfWork unitOfWork,
    ICaseAccessPolicy policy, IEllaiCaseAuthScopeFactory scopes, ICaseCorrelationStore store, IDataProtectionProvider protection)
    : ICaseCorrelationDecisionService
{
    public async Task<OrganizationDecisionOutcome> DecideAsync(ActorContext actor, Guid caseId, Guid candidateId,
        Guid commandId, OrganizationDecision decision, string rationale, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (caseId == Guid.Empty || candidateId == Guid.Empty || commandId == Guid.Empty || !Enum.IsDefined(decision) ||
            string.IsNullOrWhiteSpace(rationale) || rationale.Length > 500 || rationale != rationale.Trim()) return OrganizationDecisionOutcome.Invalid;
        ICaseCommandLease source;
        // The decision is a separate immutable record; neither case is mutated.
        // Read leases keep both authority scopes stable without taking incompatible
        // actor write locks in two separate transactions.
        try { source = await unitOfWork.BeginCaseAsync(actor, caseId, [], CaseLeaseMode.Read, ct).ConfigureAwait(false); }
        catch (KeyNotFoundException) { return OrganizationDecisionOutcome.NotFoundOrNotAuthorized; }
        catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.")
        { return OrganizationDecisionOutcome.NotFoundOrNotAuthorized; }
        await using (source.ConfigureAwait(false))
        {
            if (!(await policy.AuthorizeAllAsync(actor, caseId, [CaseOperation.ReadCase, CaseOperation.AssignCase], ct)).IsAllowed)
                return OrganizationDecisionOutcome.NotFoundOrNotAuthorized;
            var candidate = await db.Database.SqlQuery<StoredCorrelationCandidate>($"""
                SELECT candidate_id AS "CandidateId", first_case_id AS "FirstCaseId", second_case_id AS "SecondCaseId",
                    first_report_revision AS "FirstRevision", second_report_revision AS "SecondRevision",
                    recorded_at_utc AS "RecordedAtUtc", reason_codes AS "Reasons", NULL::text AS "Decision"
                FROM ellai_correlation_candidates WHERE candidate_id = {candidateId} AND (first_case_id = {caseId} OR second_case_id = {caseId})
                """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (candidate is null) return OrganizationDecisionOutcome.NotFoundOrNotAuthorized;
            var other = candidate.FirstCaseId == caseId ? candidate.SecondCaseId : candidate.FirstCaseId;
            await using var scope = scopes.CreateScope();
            ICaseCommandLease target;
            try { target = await scope.UnitOfWork.BeginCaseAsync(actor, other, [], CaseLeaseMode.Read, ct).ConfigureAwait(false); }
            catch (KeyNotFoundException) { return OrganizationDecisionOutcome.NotFoundOrNotAuthorized; }
            catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.")
            { return OrganizationDecisionOutcome.NotFoundOrNotAuthorized; }
            await using (target.ConfigureAwait(false))
            {
                if (!(await scope.Policy.AuthorizeAllAsync(actor, other, [CaseOperation.ReadCase, CaseOperation.AssignCase], ct)).IsAllowed)
                    return OrganizationDecisionOutcome.NotFoundOrNotAuthorized;
                var commandLock = $"ellai-correlation-decision:{commandId:D}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({commandLock}, 0))", ct).ConfigureAwait(false);
                var previous = await db.Database.SqlQuery<DecisionRow>($"""
                    SELECT candidate_id AS "CandidateId", actor_id AS "ActorId", decision AS "Decision", protected_rationale AS "ProtectedRationale"
                    FROM ellai_correlation_decisions WHERE command_id = {commandId}
                    """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
                if (previous is not null && (previous.CandidateId != candidateId || previous.ActorId != actor.ActorId || previous.Decision != decision.ToString()))
                    return OrganizationDecisionOutcome.Conflict;
                var protector = protection.CreateProtector("SwiftAid.Ellai.CorrelationDecision.v1", candidateId.ToString("D"));
                if (previous is not null)
                    return System.Text.Encoding.UTF8.GetString(protector.Unprotect(previous.ProtectedRationale)) == rationale
                        ? OrganizationDecisionOutcome.Replayed : OrganizationDecisionOutcome.Conflict;
                var exists = await db.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM ellai_correlation_decisions WHERE candidate_id = {candidateId}) AS \"Value\"")
                    .SingleAsync(ct).ConfigureAwait(false);
                if (exists) return OrganizationDecisionOutcome.AlreadyDecided;
                if (!await store.AreCurrentAsync(candidate, ct).ConfigureAwait(false)) return OrganizationDecisionOutcome.Stale;
                var protectedRationale = protector.Protect(System.Text.Encoding.UTF8.GetBytes(rationale));
                var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO ellai_correlation_decisions (candidate_id, command_id, actor_id, decision, protected_rationale, recorded_at_utc)
                    VALUES ({candidateId}, {commandId}, {actor.ActorId}, {decision.ToString()}, {protectedRationale}, clock_timestamp())
                    ON CONFLICT DO NOTHING
                    """, ct).ConfigureAwait(false);
                if (inserted == 0) return OrganizationDecisionOutcome.Conflict;
                await source.CommitAsync(ct).ConfigureAwait(false);
                await target.CommitAsync(ct).ConfigureAwait(false);
                return OrganizationDecisionOutcome.Recorded;
            }
        }
    }

    private sealed class DecisionRow
    {
        public Guid CandidateId { get; init; }
        public string ActorId { get; init; } = "";
        public string Decision { get; init; } = "";
        public byte[] ProtectedRationale { get; init; } = [];
    }
}
