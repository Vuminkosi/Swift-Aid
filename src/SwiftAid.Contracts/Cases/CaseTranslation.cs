namespace SwiftAid.Contracts.Cases;

public sealed record CaseTranslationItem(Guid ArtifactId, string State, string SourceLanguage, string TargetLanguage,
    string ModelId, string ModelRevision, string ReviewStatus, long ReviewVersion, IReadOnlyList<string> Findings,
    string? TranslatedText, string? FailureCode, DateTimeOffset CreatedAtUtc, string OriginalText, bool CanReview);
public sealed record CaseTranslationSnapshot(Guid CaseId, IReadOnlyList<CaseTranslationItem> Items,
    CaseClassificationAccess Access = CaseClassificationAccess.Available)
{
    public bool IsValidFor(Guid id) => id != Guid.Empty && id == CaseId && Items is { Count: <= 10 } &&
        Items.All(x => x.ArtifactId != Guid.Empty && x.ReviewVersion >= 0 && x.OriginalText is { Length: <= 6000 } &&
            x.TranslatedText is not { Length: > 16000 } && x.Findings is { Count: <= 20 } &&
            x.State is "Pending" or "Leased" or "RetryScheduled" or "Completed" or "Failed" or "Cancelled" &&
            x.ReviewStatus is "Pending" or "Unreviewed" or "Approved" or "Rejected");
}
public interface ICaseTranslationClient
{
    Task<CaseTranslationSnapshot> GetAsync(Guid caseId, CancellationToken cancellationToken = default);
    Task<string> ReviewAsync(Guid caseId, Guid artifactId, Guid decisionId, long expectedVersion, bool approve, CancellationToken cancellationToken = default);
}
