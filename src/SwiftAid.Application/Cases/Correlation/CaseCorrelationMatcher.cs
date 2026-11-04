using SwiftAid.Contracts.Commands;

namespace SwiftAid.Application.Cases.Correlation;

/// <summary>Minimal case facts loaded from the authoritative store after scope authorization.</summary>
public sealed record CorrelationCaseFacts(Guid CaseId, string OrganizationId, string WorkspaceId,
    long ReportRevision, DateTimeOffset ReportedAtUtc, Sector Sector, IReadOnlySet<string> RegionIds,
    string? StableSourceNamespace, string? StableSourceId);

public enum CorrelationReason { SameSourceIdentifier, SameSector, SharedRegion, ReportsCloseInTime, SemanticCandidate }

/// <summary>A candidate is an unconfirmed relationship, never a merge or an identity finding.</summary>
public sealed record CorrelationCandidate(Guid FirstCaseId, Guid SecondCaseId, long FirstReportRevision,
    long SecondReportRevision, DateTimeOffset LatestReportedAtUtc, IReadOnlyList<CorrelationReason> Reasons,
    double? SemanticScore, string Limitation);

/// <summary>Combines scoped authoritative facts with optional derived-index signals. The caller must
/// reauthorize both cases before projecting a candidate and must re-read their current revisions
/// before persisting or reviewing it.</summary>
public static class CaseCorrelationMatcher
{
    public static CorrelationCandidate? Compare(CorrelationCaseFacts first, CorrelationCaseFacts second,
        DateTimeOffset nowUtc, TimeSpan maximumSeparation, double? semanticScore = null)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (nowUtc.Offset != TimeSpan.Zero || maximumSeparation <= TimeSpan.Zero || maximumSeparation > TimeSpan.FromDays(30) ||
            semanticScore is < 0 or > 1 || (semanticScore.HasValue && !double.IsFinite(semanticScore.Value)))
            throw new ArgumentOutOfRangeException(nameof(maximumSeparation), "Use a UTC clock, a bounded interval, and a finite similarity score.");
        if (!Valid(first, nowUtc) || !Valid(second, nowUtc) || first.CaseId == second.CaseId ||
            !string.Equals(first.OrganizationId, second.OrganizationId, StringComparison.Ordinal) ||
            !string.Equals(first.WorkspaceId, second.WorkspaceId, StringComparison.Ordinal)) return null;

        var sameSource = !string.IsNullOrWhiteSpace(first.StableSourceNamespace) &&
            !string.IsNullOrWhiteSpace(first.StableSourceId) &&
            string.Equals(first.StableSourceNamespace, second.StableSourceNamespace, StringComparison.Ordinal) &&
            string.Equals(first.StableSourceId, second.StableSourceId, StringComparison.Ordinal);
        var sameSector = first.Sector == second.Sector;
        var closeInTime = (first.ReportedAtUtc - second.ReportedAtUtc).Duration() <= maximumSeparation;
        var sharedRegion = first.RegionIds.Any(region => second.RegionIds.Contains(region));
        // A semantic candidate is only a retrieval hint. Without either a stable source
        // identifier or the independent sector, region and time agreement, abstain.
        if (!sameSource && !(sameSector && sharedRegion && closeInTime)) return null;

        var reasons = new List<CorrelationReason>();
        if (sameSource) reasons.Add(CorrelationReason.SameSourceIdentifier);
        if (sameSector) reasons.Add(CorrelationReason.SameSector);
        if (sharedRegion) reasons.Add(CorrelationReason.SharedRegion);
        if (closeInTime) reasons.Add(CorrelationReason.ReportsCloseInTime);
        if (semanticScore.HasValue) reasons.Add(CorrelationReason.SemanticCandidate);
        var ordered = first.CaseId.CompareTo(second.CaseId) <= 0 ? (first, second) : (second, first);
        return new(ordered.Item1.CaseId, ordered.Item2.CaseId, ordered.Item1.ReportRevision, ordered.Item2.ReportRevision,
            first.ReportedAtUtc >= second.ReportedAtUtc ? first.ReportedAtUtc : second.ReportedAtUtc,
            reasons, semanticScore, "Possible relationship only; shared attributes do not establish identity or causation.");
    }

    private static bool Valid(CorrelationCaseFacts value, DateTimeOffset nowUtc) =>
        value.CaseId != Guid.Empty && value.ReportRevision > 0 &&
        !string.IsNullOrWhiteSpace(value.OrganizationId) && !string.IsNullOrWhiteSpace(value.WorkspaceId) &&
        value.ReportedAtUtc.Offset == TimeSpan.Zero && value.ReportedAtUtc <= nowUtc &&
        value.ReportedAtUtc >= nowUtc.AddDays(-30) && Enum.IsDefined(value.Sector) &&
        value.RegionIds is not null && value.RegionIds.All(region => !string.IsNullOrWhiteSpace(region));
}
