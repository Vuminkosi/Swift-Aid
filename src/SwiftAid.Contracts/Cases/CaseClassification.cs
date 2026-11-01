namespace SwiftAid.Contracts.Cases;

public enum CaseClassificationAccess { Available, Denied, Unavailable }
public sealed record CaseClassificationPattern(string Id, string Description, double SemanticScore, bool Selected);
public sealed record CaseQualityFinding(string Code, string Field, string Explanation);
public sealed record CaseQualityEvidence(string Version, IReadOnlyList<CaseQualityFinding> Findings);
public sealed record CaseTranslationProvenance(Guid ArtifactId, string Fingerprint, string SourceLanguage,
    string ModelRevision, string ReviewStatus, long ReviewVersion);
public sealed record CaseClassificationItem(Guid ResultId, long ReportRevision, string State, bool CurrentConfiguration,
    string SourceLanguage, string? AbstentionReason, DateTimeOffset CreatedAtUtc,
    IReadOnlyList<CaseClassificationPattern> Labels, CaseQualityEvidence? Quality, CaseTranslationProvenance? Translation = null);
public sealed record CaseClassificationSnapshot(Guid CaseId, IReadOnlyList<CaseClassificationItem> Items,
    CaseClassificationAccess Access = CaseClassificationAccess.Available)
{
    public bool IsValidFor(Guid id) => CaseId == id && id != Guid.Empty && Items is { Count: <= 10 } &&
        Items.All(i => i.ResultId != Guid.Empty && i.ReportRevision > 0 &&
            i.State is "Pending" or "Leased" or "RetryScheduled" or "Completed" or "Failed" &&
            i.Labels is { Count: <= 32 } && i.Labels.All(l => double.IsFinite(l.SemanticScore) && l.SemanticScore is >= 0 and <= 1) &&
            (i.Quality is null || i.Quality.Findings is { Count: <= 32 }));
}
public interface ICaseClassificationClient
{
    Task<CaseClassificationSnapshot> GetAsync(Guid caseId, CancellationToken cancellationToken = default);
}
