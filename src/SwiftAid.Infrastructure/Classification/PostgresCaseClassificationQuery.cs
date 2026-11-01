using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Classification;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Classification;

namespace SwiftAid.Infrastructure.Classification;

public sealed class PostgresCaseClassificationQuery(SwiftAidDbContext db, ICaseAccessPolicy policy,
    ICaseCommandUnitOfWork unitOfWork, IOptions<ClassificationOptions> options) : ICaseClassificationQuery
{
    public async Task<IReadOnlyList<CaseClassificationEvidence>?> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct)
    {
        if (caseId == Guid.Empty) return null;
        ICaseCommandLease lease;
        try { lease = await unitOfWork.BeginCaseAsync(actor, caseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct); }
        catch (KeyNotFoundException) { return null; }
        await using (lease)
        {
            if (!(await policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct)).IsAllowed) return null;
            var records = await (from association in db.Set<ClassificationAssociationEntity>().AsNoTracking()
                                 join result in db.Set<ClassificationResultEntity>().AsNoTracking() on association.ResultId equals result.Id
                                 join config in db.Set<ClassificationConfigurationEntity>().AsNoTracking() on association.ConfigurationId equals config.Id
                                 join header in db.ManagedCases.AsNoTracking() on association.CaseId equals header.CaseId
                                 where header.CaseId == caseId && association.ReportRevision == header.ReportRevision &&
                                     header.OrganizationId == result.OrganizationId && header.WorkspaceId == result.WorkspaceId
                                 orderby association.CreatedAtUtc descending
                                 select new { association, result, config }).Take(10).ToListAsync(ct);
            var sourceIds = records.Select(row => row.association.SourceEventId).ToArray();
            var translationIds = records.Select(row => JsonSerializer.Deserialize<ClassificationProfile>(row.config.ProfileJson)!.Translation?.ArtifactId)
                .Where(id => id.HasValue).Select(id => id!.Value).ToArray();
            var translations = await db.Set<TranslationArtifactEntity>().AsNoTracking().Where(x => translationIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            var quality = await db.Set<ReportQualityEntity>().AsNoTracking()
                .Where(q => sourceIds.Contains(q.SourceEventId) && q.Version == ReportQualityRules.Version)
                .ToDictionaryAsync(q => q.SourceEventId, q => q.AssessmentJson, ct);
            var evidence = records.Select(row =>
            {
                var profile = JsonSerializer.Deserialize<ClassificationProfile>(row.config.ProfileJson)!;
                var translationCurrent = profile.Translation is not { } translated ||
                    translations.TryGetValue(translated.ArtifactId, out var artifact) && artifact.ReviewStatus == "Approved" && artifact.ReviewVersion == translated.ReviewVersion;
                var completed = row.result.PredictionsJson is null ? null : JsonSerializer.Deserialize<ClassificationCompletion>(row.result.PredictionsJson,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return new CaseClassificationEvidence(row.result.Id, row.association.SourceEventId, row.association.ReportRevision,
                    row.result.State, translationCurrent && (options.Value.Profile with { SourceLanguage = profile.SourceLanguage, Translation = profile.Translation }).Hash == row.config.Id,
                    row.association.Reused, profile.ModelId, profile.ModelRevision, profile.TaxonomyVersion, profile.SourceLanguage,
                    row.result.AbstentionReason, row.result.FailureCode, row.result.CreatedAtUtc,
                    completed?.Predictions.Select(p => new ClassifiedLabel(p.LabelId, profile.Labels.Single(l => l.Id == p.LabelId).Description,
                        p.Score, p.Score >= profile.SelectionThreshold)).ToArray() ?? [],
                    quality.TryGetValue(row.association.SourceEventId, out var assessment)
                        ? JsonSerializer.Deserialize<ReportQualityAssessment>(assessment) : null, profile.Translation);
            }).ToArray();
            await lease.CommitAsync(ct);
            return evidence;
        }
    }
}
