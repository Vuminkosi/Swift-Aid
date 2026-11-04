using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Marks an executor that requires current-authority checks on restored history.</summary>
public interface IEllaiHistoryAuthorizationRequired { }

/// <summary>Outcome of one bounded Ellai cycle.</summary>
public sealed record EllaiCycleOutcome(
    bool Completed,
    EllaiDecision? Decision,
    string? StopReason,
    int IterationsUsed,
    int ToolCallsUsed,
    bool RepairUsed,
    string? FailureCode = null)
{
    /// <summary>Server-observed tool results for this execution. Null means no
    /// complete observation trace is available; it is never authorization.</summary>
    public IReadOnlyList<EllaiObservedToolResult>? ObservedTools { get; init; }
    public string? FrameworkSessionJson { get; init; }
}

/// <summary>Bounded tool provenance, excluding result text and arbitrary arguments.</summary>
public sealed record EllaiObservedToolResult(
    string ToolName, bool Allowed, string ReasonCode, IReadOnlyList<EvidenceRef> Evidence, bool Truncated,
    EllaiImportListScope? ImportListScope = null, EllaiCaseListQuery? CaseListQuery = null,
    string? StaticFeatureVersion = null, DateTimeOffset? CaseSnapshotExpiresAtUtc = null,
    string? ClockVersion = null, EllaiCountReplayScope? CountReplayScope = null,
    EllaiUiCatalogueReplayScope? UiCatalogueReplayScope = null, string? CapabilityVersion = null,
    string? ReceiptListDigest = null, string? ReceiptLookupId = null,
    EllaiCaseTimelineReplayScope? CaseTimelineReplayScope = null,
    EllaiUiObservationReplayScope? UiObservationReplayScope = null,
    EllaiClassificationReplayScope? ClassificationReplayScope = null,
    string? ImportDestinationsDigest = null,
    EllaiLocationReplayScope? LocationReplayScope = null, string? PublicSessionsDigest = null,
    string? PublicSessionsStatus = null)
{
    public const string CurrentStaticFeatureVersion = "state-feature/1";
    public const string CurrentClockVersion = "server-clock/1";
    public const string CurrentCapabilityVersion = "capability-descriptor/1";
    public static EllaiObservedToolResult From(EllaiToolRequest request, EllaiToolResult result)
    {
        EllaiImportListScope? scope = null;
        if (request.ToolName == EllaiToolNames.Imports && result.Allowed &&
            request.Arguments.TryGetValue("workspace_id", out var workspace) &&
            request.Arguments.TryGetValue("organization_id", out var organization) &&
            request.Arguments.TryGetValue("country", out var country))
        {
            var candidate = new EllaiImportListScope(workspace, organization, country);
            if (candidate.IsValid()) scope = candidate;
        }
        EllaiCaseListQuery? caseQuery = null;
        if (request.ToolName == EllaiToolNames.State && result.Allowed &&
            request.Arguments.TryGetValue("projection", out var projection) &&
            string.Equals(projection, "queue", StringComparison.OrdinalIgnoreCase))
        {
            var limit = request.Arguments.TryGetValue("limit", out var rawLimit) && int.TryParse(rawLimit, out var parsed) ? parsed : 20;
            var candidate = new EllaiCaseListQuery(limit, request.Arguments.GetValueOrDefault("country"),
                request.Arguments.GetValueOrDefault("workflow"), request.Arguments.GetValueOrDefault("sort"));
            if (candidate.IsValid()) caseQuery = candidate;
        }
        var staticFeature = request.ToolName == EllaiToolNames.State && result.Allowed &&
            result.ReasonCode == EllaiToolReasonCodes.Ok && result.Evidence.Count == 0 && !result.Truncated &&
            result.ResultSummary == "Current permitted feature projection." &&
            (!request.Arguments.TryGetValue("projection", out var featureProjection) ||
                string.Equals(featureProjection, "feature", StringComparison.OrdinalIgnoreCase));
        string? receiptLookupId = null;
        if (request.ToolName == EllaiToolNames.Receipts && result.ToolName == request.ToolName &&
            result.Allowed && result.DenialReason is null && result.ReasonCode == EllaiToolReasonCodes.Ok &&
            !result.Truncated && result.ItemCount == 1 && request.Arguments.Count == 1 &&
            request.Arguments.TryGetValue("receipt", out var candidateReceipt) && IsCanonicalReceiptId(candidateReceipt) &&
            result.Evidence.Count == 1 && result.Evidence[0] is { SourceKind: "Receipt", FieldPath: null } reference &&
            reference.ResourceId == candidateReceipt && reference.EvidenceId == candidateReceipt)
            receiptLookupId = candidateReceipt;
        var publicSessionsDigest = request.ToolName == EllaiToolNames.PublicSessions && result.ToolName == request.ToolName &&
                (request.Arguments.Count == 0 || request.Arguments.Count == 1 &&
                    request.Arguments.TryGetValue("status", out var status) && IsPublicSessionStatus(status)) &&
                result.Allowed && result.DenialReason is null && result.ReasonCode == EllaiToolReasonCodes.Ok &&
                (result.ItemCount == result.Evidence.Count && result.ItemCount is > 0 and <= 10 ||
                 result.ItemCount == 0 && result.Evidence.Count == 1 &&
                 result.Evidence[0] == new EvidenceRef("PublicSessionSearch", "public-sessions", "1", "search", "public-sessions/1"))
                    ? ResultFingerprint(result) : null;

        return new(request.ToolName, result.Allowed, result.ReasonCode, result.Evidence.ToArray(), result.Truncated,
            scope, caseQuery, staticFeature ? CurrentStaticFeatureVersion : null,
            request.ToolName is EllaiToolNames.Topic or EllaiToolNames.Evidence or EllaiToolNames.CaseTimeline ? result.HistoryValidUntilUtc : null,
            request.ToolName == EllaiToolNames.Now && result.Allowed && result.ReasonCode == EllaiToolReasonCodes.Ok &&
                result.Evidence.Count == 0 && !result.Truncated && request.Arguments.Count == 0 &&
                result.HistoryReplayVersion == CurrentClockVersion ? CurrentClockVersion : null,
            request.ToolName == EllaiToolNames.OwnedCaseCount && result.Allowed && !result.Truncated
                ? result.CountReplayScope : null,
            request.ToolName == EllaiToolNames.UiCatalogue && result.Allowed && !result.Truncated
                ? result.UiCatalogueReplayScope : null,
            request.ToolName == EllaiToolNames.Capabilities && result.ToolName == request.ToolName &&
                result.Allowed && result.DenialReason is null && request.Arguments.Count == 0 &&
                result.ReasonCode == EllaiToolReasonCodes.Ok && result.Evidence.Count == 0 && !result.Truncated &&
                result.ResultSummary == "Current capability descriptors." && result.ItemCount == 1
                    ? CurrentCapabilityVersion : null,
            request.ToolName == EllaiToolNames.Receipts && result.ToolName == request.ToolName &&
                (request.Arguments.Count == 0 || receiptLookupId is not null) && result.Allowed && result.DenialReason is null &&
                result.ReasonCode == EllaiToolReasonCodes.Ok && !result.Truncated &&
                result.ItemCount == result.Evidence.Count && result.Evidence.Count <= 20
                    ? ReceiptListFingerprint(result) : null,
            receiptLookupId,
            request.ToolName == EllaiToolNames.CaseTimeline && result.Allowed && !result.Truncated
                ? result.CaseTimelineReplayScope : null,
            request.ToolName == EllaiToolNames.UiObserve && result.Allowed && !result.Truncated
                ? result.UiObservationReplayScope : null,
            request.ToolName == EllaiToolNames.ClassificationEvidence && result.ToolName == request.ToolName &&
                result.Allowed && result.DenialReason is null && result.ReasonCode == EllaiToolReasonCodes.Ok &&
                !result.Truncated && result.Evidence.Count == 0 && result.ItemCount == 1 &&
                request.Arguments.Count == 1 && request.Arguments.TryGetValue("case_id", out var rawCaseId) &&
                Guid.TryParse(rawCaseId, out var caseId) && caseId != Guid.Empty
                    ? new EllaiClassificationReplayScope(caseId, ResultFingerprint(result)) : null,
            request.ToolName == EllaiToolNames.ImportDestinations && result.ToolName == request.ToolName &&
                result.Allowed && result.DenialReason is null && result.ReasonCode == EllaiToolReasonCodes.Ok &&
                !result.Truncated && result.Evidence.Count == 0 && result.ItemCount is >= 0 and <= 20 &&
                request.Arguments.Count == 0 ? ResultFingerprint(result) : null,
            request.ToolName == EllaiToolNames.Locations && result.ToolName == request.ToolName && result.Allowed &&
                result.DenialReason is null && result.ReasonCode == EllaiToolReasonCodes.Ok && !result.Truncated &&
                result.LocationReplayScope is { } locationScope && locationScope.IsValid() &&
                locationScope.ResultDigest == ResultFingerprint(result) ? locationScope : null,
            publicSessionsDigest,
            publicSessionsDigest is not null && request.Arguments.Count == 1 &&
                request.Arguments.TryGetValue("status", out var publicStatus) && IsPublicSessionStatus(publicStatus)
                    ? publicStatus : null);
    }

    public static bool IsPublicSessionStatus(string? status) => status is "Scheduled" or "Completed" or "Cancelled";

    public static bool IsCanonicalReceiptId(string? value) => value is { Length: 37 } &&
        value.StartsWith("rcpt-", StringComparison.Ordinal) &&
        Guid.TryParseExact(value.AsSpan(5), "N", out var id) && id != Guid.Empty &&
        value == "rcpt-" + id.ToString("N");

    public static string ReceiptListFingerprint(EllaiToolResult result) => ResultFingerprint(result);

    public static string ResultFingerprint(EllaiToolResult result)
    {
        var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { result.ResultSummary, result.ItemCount, result.Evidence });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
    }
}

/// <summary>Case identity and digest for a fresh authorized timeline re-read.</summary>
public sealed record EllaiCaseTimelineReplayScope(string CaseId, string Digest)
{
    public bool IsValid() => Guid.TryParseExact(CaseId, "D", out var id) && id != Guid.Empty &&
        CaseId == id.ToString("D") && Digest is { Length: 64 } &&
        Digest.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    public static EllaiCaseTimelineReplayScope Create(string caseId, EllaiToolResult result)
    {
        var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { result.ResultSummary, result.ItemCount, result.Evidence });
        return new(caseId, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)));
    }
}

/// <summary>Authorized classification case and result fingerprint, without retaining result text.</summary>
public sealed record EllaiClassificationReplayScope(Guid CaseId, string ResultDigest)
{
    public bool IsValid() => CaseId != Guid.Empty && ResultDigest is { Length: 64 } &&
        ResultDigest.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}

/// <summary>Protected search arguments and a fingerprint of the complete bounded registry result.</summary>
public sealed record EllaiLocationReplayScope(string ProtectedArguments, string ResultDigest)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(ProtectedArguments) && ProtectedArguments.Length <= 4096 &&
        ResultDigest is { Length: 64 } && ResultDigest.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}

/// <summary>Registry-owned observation references and a fingerprint of the client report.</summary>
public sealed record EllaiUiObservationReplayScope(string Feature, string Controls, string? GoalTags, string ResultDigest)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(Feature) && Feature.Length <= 120 &&
        !string.IsNullOrWhiteSpace(Controls) && Controls.Length <= 120 &&
        (GoalTags is null || !string.IsNullOrWhiteSpace(GoalTags) && GoalTags.Length <= 120) &&
        ResultDigest is { Length: 64 } && ResultDigest.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}

/// <summary>Only the resolved feature, limit and fingerprint needed to recheck catalogue discovery.</summary>
public sealed record EllaiUiCatalogueReplayScope(string Feature, int Limit, string ResultDigest, string? GoalTag = null)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(Feature) && Feature.Length <= 120 && Limit is >= 1 and <= 20 &&
        (GoalTag is null || !string.IsNullOrWhiteSpace(GoalTag) && GoalTag.Length <= 120) &&
        ResultDigest is { Length: 64 } && ResultDigest.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    public static EllaiUiCatalogueReplayScope Create(string feature, int limit, EllaiToolResult result, string? goalTag = null)
    {
        var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { result.ResultSummary, result.ItemCount, result.Evidence });
        return new(feature, limit, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)), goalTag);
    }
}

/// <summary>Window and membership fingerprint, without storing aggregate rows or model arguments.</summary>
public sealed record EllaiCountReplayScope(int WindowDays, string MembershipDigest)
{
    public bool IsValid() => WindowDays is >= 1 and <= 365 && MembershipDigest is { Length: 64 } &&
        MembershipDigest.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    public static EllaiCountReplayScope Create(int windowDays, IEnumerable<Guid> caseIds)
    {
        var membership = string.Join("\n", caseIds.Select(id => id.ToString("D")).Order(StringComparer.Ordinal));
        return new(windowDays, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(membership))));
    }
}

/// <summary>Only the existing import reader's scope keys, never arbitrary model arguments.</summary>
public sealed record EllaiImportListScope(string WorkspaceId, string OrganizationId, string CountryCode)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(WorkspaceId) && WorkspaceId.Length <= 256 &&
        !string.IsNullOrWhiteSpace(OrganizationId) && OrganizationId.Length <= 256 &&
        CountryCode is { Length: 2 } && CountryCode.All(character => character is >= 'A' and <= 'Z');
}

/// <summary>The existing owned-case query options needed to recheck list membership.</summary>
public sealed record EllaiCaseListQuery(int Limit, string? Country, string? Workflow, string? Sort)
{
    public bool IsValid() => Limit is >= 1 and <= 20 &&
        (Country is null || Country.Length == 2 && Country.All(character => character is >= 'A' and <= 'Z')) &&
        (Workflow is null or "Reported" or "UnderReview" or "Active" or "AwaitingFollowUp" or "Resolved") &&
        (Sort is null or "latest-reported" or "latest-updated");
}
