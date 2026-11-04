using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Classification;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Classification;

namespace SwiftAid.Infrastructure.Classification;

public sealed class CaseTranslationService(SwiftAidDbContext db, ICaseAccessPolicy policy, ICaseCommandUnitOfWork unitOfWork,
    TranslationRegistry registry, IOptions<TranslationOptions> configured, TimeProvider clock)
{
    public async Task<IReadOnlyList<TranslationEvidence>?> ReadAsync(ActorContext actor, Guid caseId, CancellationToken ct)
    {
        ICaseCommandLease lease;
        try { lease = await unitOfWork.BeginCaseAsync(actor, caseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct); }
        catch (KeyNotFoundException) { return null; }
        await using (lease)
        {
            if (!(await policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct)).IsAllowed) return null;
            var header = await db.ManagedCases.AsNoTracking().SingleAsync(x => x.CaseId == caseId, ct);
            var artifacts = await (from association in db.Set<TranslationAssociationEntity>().AsNoTracking()
                join artifact in db.Set<TranslationArtifactEntity>().AsNoTracking() on association.ArtifactId equals artifact.Id
                where association.CaseId == caseId && association.ReportRevision == header.ReportRevision &&
                    artifact.OrganizationId == header.OrganizationId && artifact.WorkspaceId == header.WorkspaceId
                orderby artifact.CreatedAtUtc descending select artifact).Take(10).ToListAsync(ct);
            var canReview = registry.Allows(header.OrganizationId) && configured.Value.Reviewers.Contains(actor.ActorId, StringComparer.Ordinal);
            var result = artifacts.Select(x => new TranslationEvidence(x.Id, x.State, x.SourceLanguage, "eng", TranslationIdentity.Model,
                x.ModelRevision, x.ReviewStatus, x.ReviewVersion, JsonSerializer.Deserialize<string[]>(x.FindingsJson) ?? [],
                x.ProtectedTranslation is null ? null : registry.ReadTranslation(x), x.FailureCode, x.CreatedAtUtc, registry.ReadSource(x), canReview)).ToArray();
            await lease.CommitAsync(ct); return result;
        }
    }

    public async Task<string> ReviewAsync(ActorContext actor, Guid caseId, Guid artifactId, TranslationReviewRequest request, CancellationToken ct)
    {
        if (request.DecisionId == Guid.Empty || request.ExpectedVersion < 0) throw new ArgumentException("Invalid review identity.");
        await using var lease = await unitOfWork.BeginCaseAsync(actor, caseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct);
        if (!(await policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct)).IsAllowed ||
            !configured.Value.Reviewers.Contains(actor.ActorId, StringComparer.Ordinal)) throw new UnauthorizedAccessException();
        var header = await db.ManagedCases.AsNoTracking().SingleAsync(x => x.CaseId == caseId, ct);
        if (!registry.Allows(header.OrganizationId) || !await db.Set<TranslationAssociationEntity>().AnyAsync(x => x.CaseId == caseId &&
            x.ReportRevision == header.ReportRevision && x.ArtifactId == artifactId, ct)) throw new UnauthorizedAccessException();
        var artifact = await registry.LockedAsync(artifactId, ct);
        if (artifact.OrganizationId != header.OrganizationId || artifact.WorkspaceId != header.WorkspaceId) throw new UnauthorizedAccessException();
        var prior = await db.Set<TranslationReviewEntity>().FindAsync([request.DecisionId], ct);
        if (prior is not null)
            return prior.ArtifactId == artifactId && prior.ActorId == actor.ActorId && prior.Approved == request.Approve &&
                prior.Version == request.ExpectedVersion + 1 ? "Replayed" : "Conflict";
        if (artifact.State != "Completed" || artifact.ReviewVersion != request.ExpectedVersion) return "Conflict";
        artifact.ReviewStatus = request.Approve ? "Approved" : "Rejected"; artifact.ReviewVersion++;
        db.Add(new TranslationReviewEntity { DecisionId = request.DecisionId, ArtifactId = artifactId, ActorId = actor.ActorId,
            Approved = request.Approve, Version = artifact.ReviewVersion, RecordedAtUtc = clock.GetUtcNow() });
        // Approval applies to this scoped immutable artifact. Replay every current associated source.
        var sources = await (from association in db.Set<TranslationAssociationEntity>().AsNoTracking()
                             join current in db.ManagedCases.AsNoTracking() on association.CaseId equals current.CaseId
                             where association.ArtifactId == artifactId && association.ReportRevision == current.ReportRevision &&
                                 current.WorkspaceId == artifact.WorkspaceId && current.OrganizationId == artifact.OrganizationId
                             orderby current.CreatedAtUtc descending, current.CaseId, association.SourceEventId
                             select association.SourceEventId).ToArrayAsync(ct);
        foreach (var source in sources)
            db.Add(new TranslationOutboxEntity { Id = Guid.NewGuid(), ArtifactId = artifactId, SourceEventId = source,
                ReviewVersion = artifact.ReviewVersion, AvailableAtUtc = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct); await lease.CommitAsync(ct); return "Recorded";
    }
}
