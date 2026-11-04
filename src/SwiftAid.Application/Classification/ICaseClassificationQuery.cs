using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Classification;

public sealed record ClassifiedLabel(string Id, string Description, double SemanticScore, bool Selected);
public sealed record CaseClassificationEvidence(Guid ResultId, Guid SourceEventId, long ReportRevision,
    string State, bool CurrentConfiguration, bool Reused, string ModelId, string ModelRevision,
    string TaxonomyVersion, string SourceLanguage, string? AbstentionReason, string? FailureCode,
    DateTimeOffset CreatedAtUtc, IReadOnlyList<ClassifiedLabel> Labels, ReportQualityAssessment? Quality = null,
    TranslationProvenance? Translation = null);

public interface ICaseClassificationQuery
{
    /// <summary>Null means missing or not authorized. Never starts inference or returns another case's evidence.</summary>
    Task<IReadOnlyList<CaseClassificationEvidence>?> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct);
}
