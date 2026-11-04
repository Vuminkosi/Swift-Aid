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
}

/// <summary>Bounded tool provenance, excluding result text and arbitrary arguments.</summary>
public sealed record EllaiObservedToolResult(
    string ToolName, bool Allowed, string ReasonCode, IReadOnlyList<EvidenceRef> Evidence, bool Truncated,
    EllaiImportListScope? ImportListScope = null, EllaiCaseListQuery? CaseListQuery = null,
    string? StaticFeatureVersion = null, DateTimeOffset? CaseSnapshotExpiresAtUtc = null,
    string? ClockVersion = null, EllaiCountReplayScope? CountReplayScope = null,
    EllaiUiCatalogueReplayScope? UiCatalogueReplayScope = null, string? CapabilityVersion = null,
    string? ReceiptListDigest = null, string? ReceiptLookupId = null, string? CaseListDigest = null,
    EllaiRetainedClassificationFilter? RetainedClassificationFilter = null, string? QueueProjectionVersion = null,
    EllaiRetainedCaseReplay? RetainedCaseReplay = null,
    EllaiUiObservationReplayScope? UiObservationReplayScope = null)
{
    public const string CurrentStaticFeatureVersion = "state-feature/1";
    public const string CurrentClockVersion = "server-clock/1";
    public const string CurrentCapabilityVersion = "capability-descriptor/1";
    public const string CurrentQueueProjectionVersion = "state-queue/3";
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
                request.Arguments.GetValueOrDefault("workflow"), request.Arguments.GetValueOrDefault("sort"),
                request.Arguments.GetValueOrDefault("place_id"), request.Arguments.GetValueOrDefault("reported_from"),
                request.Arguments.GetValueOrDefault("reported_before"), request.Arguments.GetValueOrDefault("group_by"));
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
        return new(request.ToolName, result.Allowed, result.ReasonCode, result.Evidence.ToArray(), result.Truncated,
            scope, caseQuery, staticFeature ? CurrentStaticFeatureVersion : null,
            request.ToolName is EllaiToolNames.Topic or EllaiToolNames.Evidence ? result.HistoryValidUntilUtc : null,
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
            (caseQuery?.PlaceId is not null || caseQuery?.GroupBy is not null) && result.Allowed && result.DenialReason is null &&
                result.ReasonCode == EllaiToolReasonCodes.Ok && result.ItemCount == result.Evidence.Count
                    ? CaseListFingerprint(result) : null,
            request.ToolName == EllaiToolNames.Evidence && result.ToolName == request.ToolName && result.Allowed &&
                result.DenialReason is null && result.ReasonCode == EllaiToolReasonCodes.Ok && !result.Truncated &&
                result.ItemCount == result.Evidence.Count && result.HistoryValidUntilUtc is not null &&
                request.Arguments.TryGetValue("classification_filter", out var retainedFilter) &&
                retainedFilter is "Classified" or "Unclassified" &&
                request.Arguments.GetValueOrDefault("field") is null
                    ? new EllaiRetainedClassificationFilter(retainedFilter, CaseListFingerprint(result)) : null,
            caseQuery is not null ? CurrentQueueProjectionVersion : null,
            EllaiRetainedCaseReplay.Create(request, result),
            request.ToolName == EllaiToolNames.UiObserve && result.Allowed && !result.Truncated
                ? result.UiObservationReplayScope : null);
    }

    public static bool IsCanonicalReceiptId(string? value) => value is { Length: 37 } &&
        value.StartsWith("rcpt-", StringComparison.Ordinal) &&
        Guid.TryParseExact(value.AsSpan(5), "N", out var id) && id != Guid.Empty &&
        value == "rcpt-" + id.ToString("N");

    public static string ReceiptListFingerprint(EllaiToolResult result)
    {
        var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { result.ResultSummary, result.ItemCount, result.Evidence });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
    }

    public static string CaseListFingerprint(EllaiToolResult result)
    {
        var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            ResultSummary = result.ResultSummary
                .Replace(" Result references retained for this session; recall with ellai_evidence(ref=case-results).", "", StringComparison.Ordinal)
                .Replace(" Supporting case references were retained for this session.", "", StringComparison.Ordinal)
                .Replace(" A bounded subset of supporting case references was retained; later case-level follow-up may be incomplete.", "", StringComparison.Ordinal)
                .Replace(" Supporting case references could not be retained; do not claim they were remembered.", "", StringComparison.Ordinal)
                .Replace(" Result references could not be retained; do not claim they were remembered.", "", StringComparison.Ordinal),
            result.ItemCount, result.Evidence, result.Truncated
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
    }

    /// <summary>Fingerprint report observations without persisting their text in durable provenance.</summary>
    public static string CaseReportFingerprint(EllaiToolResult result)
    {
        var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { result.ItemCount, result.Evidence, result.Truncated });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
    }

    /// <summary>Fingerprint the safe attribution projection without storing display names in replay provenance.</summary>
    public static string CaseSubmitterFingerprint(EllaiToolResult result)
    {
        var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            result.ResultSummary,
            result.ItemCount,
            result.Evidence,
            result.Truncated
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
    }
}

/// <summary>Bounded references and digest for a fresh UI observation; no entered values are stored.</summary>
public sealed record EllaiUiObservationReplayScope(string Feature, IReadOnlyList<string> Controls, string Digest,
    bool PresentationMayChange = false)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(Feature) && Feature.Length <= 120 &&
        Controls is { Count: > 0 and <= 20 } && Controls.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 120) &&
        Controls.Distinct(StringComparer.Ordinal).Count() == Controls.Count && Digest is { Length: 64 } &&
        Digest.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}

/// <summary>Bounded arguments and fingerprint for revalidating a partial retained result.</summary>
public sealed record EllaiRetainedCaseReplay(string Reference, string? Field, string Digest,
    string? ClassificationFilter = null, string? Country = null, string? PlaceId = null, string? CaseId = null,
    string? GroupBy = null)
{
    public bool IsValid() => Reference is EllaiCaseResultMemory.Reference or EllaiCaseResultMemory.OriginalReference &&
        Field is null or "country" or "workflow" or "observations" or "submitter" &&
        (ClassificationFilter is null or "Classified" or "Unclassified") &&
        (ClassificationFilter is null || Field == "observations" || Field is null && PlaceId is null && Country is null) &&
        ((Country is null) == (PlaceId is null)) &&
        (Country is null || Country.Length == 2 && Country.All(character => character is >= 'A' and <= 'Z') &&
            PlaceId is { Length: >= 1 and <= 120 } && PlaceId == PlaceId.Trim() && (Field is null or "observations")) &&
        (CaseId is null || Field == "submitter" && Guid.TryParseExact(CaseId, "D", out var caseId) &&
            caseId != Guid.Empty && CaseId == caseId.ToString("D")) &&
        (GroupBy is null || (GroupBy is "region" or "locality") && Field is null && PlaceId is null && CaseId is null) && Digest is { Length: 64 } &&
        Digest.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    public static EllaiRetainedCaseReplay? Create(EllaiToolRequest request, EllaiToolResult result)
    {
        if (request.ToolName is not (EllaiToolNames.Topic or EllaiToolNames.Evidence) ||
            result.ToolName != request.ToolName || !result.Allowed ||
            (!result.Truncated && !request.Arguments.ContainsKey("classification_filter") && !request.Arguments.ContainsKey("place_id") && !request.Arguments.ContainsKey("group_by") &&
                request.Arguments.GetValueOrDefault("field") is not ("observations" or "submitter")) || result.DenialReason is not null ||
            result.ReasonCode != EllaiToolReasonCodes.Ok || result.HistoryValidUntilUtc is null ||
            result.ItemCount != result.Evidence.Count || result.Evidence.Count > 20 ||
            !request.Arguments.TryGetValue("ref", out var reference) ||
            request.Arguments.Keys.Any(key => key != "ref" && (request.ToolName != EllaiToolNames.Evidence ||
                key is not ("field" or "classification_filter" or "group_by" or "country" or "place_id" or "case_id"))))
            return null;
        var classificationFilter = request.Arguments.GetValueOrDefault("classification_filter");
        var field = request.Arguments.GetValueOrDefault("field");
        if (classificationFilter is not null && (classificationFilter is not ("Classified" or "Unclassified") ||
            field is not null and not "observations" || request.Arguments.ContainsKey("place_id") && field != "observations")) return null;
        var country = request.Arguments.GetValueOrDefault("country");
        var placeId = request.Arguments.GetValueOrDefault("place_id");
        var caseId = request.Arguments.GetValueOrDefault("case_id");
        var groupBy = request.Arguments.GetValueOrDefault("group_by");
        if ((country is null) != (placeId is null) || placeId is not null &&
            (country is not { Length: 2 } || country.Any(character => character is < 'A' or > 'Z') ||
                placeId.Length is < 1 or > 120 || placeId != placeId.Trim() || field is not null and not "observations")) return null;
        if (caseId is not null && (field != "submitter" || !Guid.TryParseExact(caseId, "D", out var parsedCaseId) ||
            parsedCaseId == Guid.Empty || caseId != parsedCaseId.ToString("D"))) return null;
        if (groupBy is not null && (groupBy is not ("region" or "locality") || field is not null || placeId is not null || caseId is not null)) return null;
        var candidate = new EllaiRetainedCaseReplay(reference, request.Arguments.GetValueOrDefault("field"),
            field switch
            {
                "observations" => EllaiObservedToolResult.CaseReportFingerprint(result),
                "submitter" => EllaiObservedToolResult.CaseSubmitterFingerprint(result),
                _ => EllaiObservedToolResult.CaseListFingerprint(result)
            },
            classificationFilter, country, placeId, caseId, groupBy);
        return candidate.IsValid() ? candidate : null;
    }
}

/// <summary>Revalidates membership, including previously excluded cases, before replaying a filtered retained set.</summary>
public sealed record EllaiRetainedClassificationFilter(string Filter, string Digest)
{
    public bool IsValid() => Filter is "Classified" or "Unclassified" && Digest is { Length: 64 } &&
        Digest.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
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

/// <summary>The existing authorized case-query options needed to recheck list membership.</summary>
public sealed record EllaiCaseListQuery(int Limit, string? Country, string? Workflow, string? Sort,
    string? PlaceId = null, string? ReportedFrom = null, string? ReportedBefore = null, string? GroupBy = null)
{
    public bool IsValid() => Limit is >= 1 and <= 20 &&
        (Country is null || Country.Length == 2 && Country.All(character => character is >= 'A' and <= 'Z')) &&
        (Workflow is null or "Reported" or "UnderReview" or "Active" or "AwaitingFollowUp" or "Resolved") &&
        (Sort is null or "latest-reported" or "latest-updated") &&
        (GroupBy is null || (GroupBy is "region" or "locality") && Country is not null && PlaceId is null && Sort != "latest-updated") &&
        (PlaceId is null || Country is not null && Workflow is null && Sort == "latest-reported" &&
            !string.IsNullOrWhiteSpace(PlaceId) && PlaceId.Length <= 120 && PlaceId == PlaceId.Trim()) &&
        TryGetReportedRange(out _, out _);

    public bool TryGetReportedRange(out DateTimeOffset? fromUtc, out DateTimeOffset? beforeUtc)
    {
        fromUtc = null;
        beforeUtc = null;
        if (ReportedFrom is null && ReportedBefore is null) return true;
        if (ReportedFrom is null || ReportedBefore is null ||
            !DateOnly.TryParseExact(ReportedFrom, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var from) ||
            !DateOnly.TryParseExact(ReportedBefore, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var before) ||
            before <= from || before.DayNumber - from.DayNumber > 365)
            return false;
        fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        beforeUtc = new DateTimeOffset(before.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        return true;
    }
}
