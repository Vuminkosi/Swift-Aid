using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Surveillance;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Surveillance;

namespace SwiftAid.Infrastructure.Surveillance;

public sealed class PostgresSignalReviewService(SwiftAidDbContext db, IServiceScopeFactory scopes,
    IOptions<SurveillanceOptions> options, TimeProvider clock) : ISignalReviewService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] States = ["AwaitingTriage", "UnderVerification", "Verified", "Dismissed", "Monitoring", "Escalated"];
    public bool Enabled => options.Value.Enabled;

    public async Task<IReadOnlyList<SignalDossier>?> ForCaseAsync(ActorContext actor, Guid caseId, CancellationToken ct)
    {
        if (!Enabled) return [];
        var ids = await (from evidence in db.Set<DetectionEvidenceEntity>().AsNoTracking()
                         join signal in db.Set<SurveillanceSignalEntity>().AsNoTracking() on evidence.AssessmentId equals signal.AssessmentId
                         where evidence.CaseId == caseId
                         orderby signal.WindowEndUtc descending, signal.Id
                         select signal.Id).Take(10).ToArrayAsync(ct);
        var dossiers = new List<SignalDossier>();
        foreach (var id in ids)
        {
            var dossier = await GetAsync(actor, id, ct);
            if (dossier is not null) dossiers.Add(dossier);
        }
        return dossiers;
    }

    public Task<SignalDossier?> GetAsync(ActorContext actor, Guid signalId, CancellationToken ct) =>
        WithEvidenceAuthorityAsync<SignalDossier>(actor, signalId, false, async (signal, assessment) =>
        {
            var reviews = await db.Set<SurveillanceReviewEntity>().AsNoTracking().Where(r => r.SignalId == signalId && r.AssessmentId == signal.AssessmentId)
                .OrderByDescending(r => r.Version).Take(20).ToArrayAsync(ct);
            return new(signal.Id, signal.Version, signal.AssessmentId, signal.ReviewState,
                JsonSerializer.Deserialize<DetectionAssessment>(assessment.AssessmentJson, Json)!,
                reviews.Select(r => new SignalReviewDecision(r.CommandId, r.AssessmentId, r.Version, r.ActorId, r.State, r.Rationale, r.RecordedAtUtc)).ToArray());
        }, ct);

    public async Task<SignalDecisionResult> DecideAsync(SignalDecisionCommand command, ActorContext actor, CancellationToken ct)
    {
        if (command.CommandId == Guid.Empty || command.ExpectedVersion < 1 || command.AssessmentId == Guid.Empty ||
            !States.Contains(command.State, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(command.Rationale) || command.Rationale.Length > 1000)
            return new("Invalid");
        return await WithEvidenceAuthorityAsync<SignalDecisionResult>(actor, command.SignalId, true, async (snapshot, _) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"surveillance:" + snapshot.WorkspaceId + ":" + snapshot.OrganizationId}, 0))", ct);
            var current = await db.Set<SurveillanceSignalEntity>().SingleAsync(s => s.Id == command.SignalId, ct);
            var prior = await db.Set<SurveillanceReviewEntity>().FindAsync([command.CommandId], ct);
            if (prior is not null)
                return prior.SignalId == command.SignalId && prior.AssessmentId == command.AssessmentId && prior.ActorId == actor.ActorId &&
                    prior.Version == command.ExpectedVersion + 1 && prior.State == command.State && prior.Rationale == command.Rationale
                    ? new("Completed", prior.Version) : new("Conflict");
            if (current.Version != command.ExpectedVersion || current.AssessmentId != command.AssessmentId || !current.Candidate ||
                current.AssessmentId != snapshot.AssessmentId) return new("Conflict");
            // A verification decision confirms an officer's review of this evidence, never an outbreak declaration.
            if (command.State == "Verified" && current.ReviewState != "UnderVerification") return new("InvalidTransition");
            current.Version++;
            current.ReviewState = command.State;
            db.Add(new SurveillanceReviewEntity { CommandId = command.CommandId, SignalId = current.Id, AssessmentId = current.AssessmentId,
                Version = current.Version, ActorId = actor.ActorId, State = command.State, Rationale = command.Rationale, RecordedAtUtc = clock.GetUtcNow() });
            db.Add(new SurveillanceEventEntity { Id = Guid.NewGuid(), SignalId = current.Id, Version = current.Version,
                EventType = "SignalReviewed", RecordedAtUtc = clock.GetUtcNow(), AvailableAtUtc = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new("Completed", current.Version);
        }, ct) ?? new("Unavailable");
    }

    private async Task<T?> WithEvidenceAuthorityAsync<T>(ActorContext actor, Guid signalId, bool review,
        Func<SurveillanceSignalEntity, DetectionAssessmentEntity, Task<T>> action, CancellationToken ct) where T : class
    {
        if (!Enabled || signalId == Guid.Empty) return null;
        options.Value.Validate();
        var signal = await db.Set<SurveillanceSignalEntity>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == signalId, ct);
        if (signal is null || !options.Value.Workspaces.Contains(signal.WorkspaceId) || !options.Value.Organizations.Contains(signal.OrganizationId)) return null;
        var assessment = await db.Set<DetectionAssessmentEntity>().AsNoTracking().SingleAsync(a => a.Id == signal.AssessmentId, ct);
        var evidence = await db.Set<DetectionEvidenceEntity>().AsNoTracking().Where(e => e.AssessmentId == assessment.Id).OrderBy(e => e.CaseId).Take(33).ToArrayAsync(ct);
        if (evidence.Length is 0 or > 32) return null;
        var held = new List<(AsyncServiceScope Scope, ICaseCommandLease Lease)>();
        try
        {
            // Hold every contributing case's existing read-authority lease until the dossier/decision
            // has been produced. Counts and baselines cannot reveal an inaccessible case.
            foreach (var item in evidence)
            {
                var scope = scopes.CreateAsyncScope();
                ICaseCommandLease lease;
                try { lease = await scope.ServiceProvider.GetRequiredService<ICaseCommandUnitOfWork>()
                    .BeginCaseAsync(actor, item.CaseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct); }
                catch { await scope.DisposeAsync(); throw; }
                held.Add((scope, lease));
                var policy = scope.ServiceProvider.GetRequiredService<ICaseAccessPolicy>();
                var operations = review ? new[] { CaseOperation.ReadCase, CaseOperation.ReadLinkedSignalEvidence, CaseOperation.ReviewProtectedAction }
                    : [CaseOperation.ReadCase, CaseOperation.ReadLinkedSignalEvidence];
                if (!(await policy.AuthorizeAllAsync(actor, item.CaseId, operations, ct)).IsAllowed) return null;
                var current = await scope.ServiceProvider.GetRequiredService<SwiftAidDbContext>().ManagedCases.AsNoTracking().SingleAsync(c => c.CaseId == item.CaseId, ct);
                if (current.ReportRevision != item.ReportRevision || current.WorkspaceId != signal.WorkspaceId ||
                    current.OrganizationId != signal.OrganizationId || current.SensitivityCode != "standard") return null;
            }
            if (!await db.Set<SurveillanceSignalEntity>().AnyAsync(s => s.Id == signalId && s.Version == signal.Version, ct)) return null;
            return await action(signal, assessment);
        }
        catch (KeyNotFoundException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (InvalidOperationException error) when (error.Message is
            "The case authority scope is unavailable." or "The current case authority scope is unavailable.")
        {
            // The authority helper also rejects missing or revoked actors before a lease exists.
            // Return the same unavailable result as policy denial; other failures must propagate.
            return null;
        }
        finally
        {
            foreach (var (scope, lease) in held.AsEnumerable().Reverse())
            {
                await lease.DisposeAsync();
                await scope.DisposeAsync();
            }
        }
    }
}
