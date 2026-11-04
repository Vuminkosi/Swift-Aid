using System.Globalization;
using System.Text.Json;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Session-local result references. PostgreSQL topics hold no case narratives;
/// every recall rechecks current Search and ReadCase authorization.</summary>
public sealed class EllaiCaseResultMemory(EllaiTopicService topics, ICaseQueueQuery queue,
    IEllaiExactReferenceIndex? index = null, ICaseOccurrenceLocationQuery? occurrenceLocations = null,
    ICaseReportReadQuery? caseReports = null, ICaseReportAttributionQuery? caseAttributions = null)
{
    public const string Reference = "case-results";
    public const string OriginalReference = "case-results-original";
    public const string TopicTitle = "Ellai latest case result references";
    private const int MaximumSets = 8;
    private const int MaximumWriteAttempts = 12;
    private sealed record Snapshot(string ActorId, string ScopeEpoch, DateTimeOffset ExpiresAtUtc,
        IReadOnlyList<CaseReference> Cases, bool? SearchIncomplete = null, bool? UnknownLocations = null,
        string? SnapshotId = null, IReadOnlyList<Snapshot>? Previous = null, bool? HistoryTruncated = null);
    private sealed record CaseReference(Guid Id, long Version);

    public async Task RememberAsync(EllaiToolContext context, IReadOnlyList<CaseQueueItem> items,
        CancellationToken ct, bool searchIncomplete = false, bool unknownLocations = false)
    {
        ct.ThrowIfCancellationRequested();
        if (context.Actor is null) return;
        if (items.Count > 20 || items.Any(item => item.CaseId == Guid.Empty) || items.Select(item => item.CaseId).Distinct().Count() != items.Count)
            throw new ArgumentException("Case result references must be distinct and nonempty.", nameof(items));
        var snapshot = new Snapshot(context.Actor.ActorId, context.ScopeEpoch, DateTimeOffset.UtcNow.AddHours(24),
            items.Take(20).Select(item => new CaseReference(item.CaseId, item.AggregateVersion)).ToArray(),
            searchIncomplete, unknownLocations, Guid.NewGuid().ToString("N"));
        for (var attempt = 0; attempt < MaximumWriteAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var existing = await topics.GetByTitleAsync(context.SessionId, TopicTitle, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (existing is null)
            {
                var first = JsonSerializer.Serialize(snapshot with { Previous = [], HistoryTruncated = false });
                var created = await topics.TryCreateAsync(context.SessionId, TopicTitle, [Reference], first, "case-results/3", ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (created) return;
                continue;
            }
            if (existing.Title != TopicTitle || existing.EntityVersion is not ("case-results/1" or "case-results/2" or "case-results/3") ||
                existing.EntityRef is not { Length: <= 16000 } source)
                throw new InvalidOperationException("The case result history is unavailable.");
            Snapshot? prior;
            try { prior = JsonSerializer.Deserialize<Snapshot>(source); }
            catch (JsonException) { throw new InvalidOperationException("The case result history is unavailable."); }
            if (prior is null || !IsValidSnapshot(prior, existing.EntityVersion == "case-results/3"))
                throw new InvalidOperationException("The case result history is unavailable.");
            var sameScope = prior.ActorId == context.Actor.ActorId && prior.ScopeEpoch == context.ScopeEpoch;
            var previous = sameScope ? (prior.Previous ?? []).Append(prior with { Previous = null, HistoryTruncated = null }).ToArray() : [];
            var retained = previous.Where(item => item.ExpiresAtUtc > DateTimeOffset.UtcNow).TakeLast(MaximumSets - 1).ToArray();
            var incompleteHistory = !sameScope || existing.EntityVersion != "case-results/3" ||
                prior.HistoryTruncated == true || retained.Length != previous.Length;
            var serialized = JsonSerializer.Serialize(snapshot with { Previous = retained, HistoryTruncated = incompleteHistory });
            if (serialized.Length > 16000) throw new InvalidOperationException("The case result history is too large.");
            var updated = await topics.CompareExchangePayloadAsync(context.SessionId, existing.TopicId, source,
                existing.EntityVersion, serialized, "case-results/3", ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (updated) return;
        }
        throw new InvalidOperationException("The case result history was concurrently updated.");
    }

    public async Task<EllaiToolResult> RecallAsync(
        EllaiToolContext context, CancellationToken ct, string? field = null, string? classificationFilter = null,
        bool original = false, string? country = null, string? placeId = null, Guid? caseId = null)
    {
        ct.ThrowIfCancellationRequested();
        if (field is not null && field is not ("country" or "workflow" or "observations" or "submitter"))
            return new(EllaiToolNames.Topic, false, "Unsupported case-result field selector.", [], "", 0, false,
                EllaiToolReasonCodes.InvalidArguments);
        if (classificationFilter is not null && (classificationFilter is not ("Classified" or "Unclassified") ||
            field is not null and not "observations" || placeId is not null && field != "observations"))
            return new(EllaiToolNames.Topic, false, "Unsupported case-result classification filter.", [], "", 0, false,
                EllaiToolReasonCodes.InvalidArguments);
        if ((country is null) != (placeId is null) ||
            country is not null && (country.Length != 2 || country.Any(character => character is < 'A' or > 'Z')) ||
            placeId is not null && (placeId.Length is < 1 or > 120 || placeId != placeId.Trim() || field is not null and not "observations"))
            return new(EllaiToolNames.Topic, false, "An exact occurrence-place filter requires a country and cannot be combined with a field selector.", [], "", 0, false,
                EllaiToolReasonCodes.InvalidArguments);
        if (caseId == Guid.Empty || caseId is not null && field != "submitter")
            return new(EllaiToolNames.Topic, false, "A case_id is valid only for submitter attribution on a retained case.", [], "", 0, false,
                EllaiToolReasonCodes.InvalidArguments);
        if (context.Actor is null) return Unavailable();
        EllaiTopicEntry? indexedTopic = null;
        if (index is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
            try
            {
                var hit = await index.FindExactAsync(Reference, context.SessionId, context.ScopeEpoch, timeout.Token).ConfigureAwait(false);
                // An index implementation can return after its cancellation token
                // was signalled. In that case resolve through the authoritative
                // topic store instead of accepting the overdue derived lookup.
                timeout.Token.ThrowIfCancellationRequested();
                if (hit is not null && hit.Id == Reference && hit.SessionId == context.SessionId &&
                    hit.ScopeEpoch == context.ScopeEpoch && hit.ExpiresAtUtc > DateTimeOffset.UtcNow)
                {
                    var candidate = await topics.GetAsync(context.SessionId, hit.TopicId, ct).ConfigureAwait(false);
                    if (candidate?.Title == TopicTitle && candidate.EntityRef is { } source &&
                        SourceVersion(source) == hit.SourceVersion) indexedTopic = candidate;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* Derived cache cannot block authoritative recall. */ }
        }
        var topic = indexedTopic is not null
            ? new EllaiTopic(indexedTopic.TopicId, indexedTopic.Title, indexedTopic.Aliases, indexedTopic.EntityRef,
                indexedTopic.EntityVersion, indexedTopic.CoverageState, indexedTopic.CheckpointId)
            : (await topics.ResolveAsync(context.SessionId, Reference, ct).ConfigureAwait(false)).Matches
                .SingleOrDefault(item => item.Title == TopicTitle);
        ct.ThrowIfCancellationRequested();
        Snapshot? snapshot;
        try
        {
            snapshot = topic?.EntityVersion is "case-results/1" or "case-results/2" or "case-results/3" && topic.EntityRef is { Length: <= 16000 } json
                ? JsonSerializer.Deserialize<Snapshot>(json) : null;
        }
        catch (JsonException) { return Unavailable(); }
        if (snapshot is null || !IsValidSnapshot(snapshot, topic?.EntityVersion == "case-results/3") ||
            topic?.EntityVersion is "case-results/2" or "case-results/3" && (snapshot.SearchIncomplete is null || snapshot.UnknownLocations is null)
            || snapshot.ActorId != context.Actor.ActorId || snapshot.ScopeEpoch != context.ScopeEpoch
            || snapshot.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return Unavailable();
        if (original)
        {
            if (topic?.EntityVersion != "case-results/3" || snapshot.HistoryTruncated != false) return OriginalUnavailable();
            snapshot = snapshot.Previous is { Count: > 0 } ? snapshot.Previous[0] : snapshot;
            if (!IsValidSnapshot(snapshot, false) || snapshot.ActorId != context.Actor.ActorId ||
                snapshot.ScopeEpoch != context.ScopeEpoch || snapshot.ExpiresAtUtc <= DateTimeOffset.UtcNow) return OriginalUnavailable();
        }
        var completeness = snapshot.SearchIncomplete is null || snapshot.UnknownLocations is null
            ? "The original search completeness was not recorded. "
            : (snapshot.SearchIncomplete.Value ? "The original search or result was incomplete. " : "") +
              (snapshot.UnknownLocations.Value ? "Some readable cases had unknown or incomplete locations. " : "");
        var incomplete = snapshot.SearchIncomplete != false || snapshot.UnknownLocations != false;
        if (snapshot.Cases.Count == 0)
            return new EllaiToolResult(EllaiToolNames.Topic, true, null, [],
                completeness + (classificationFilter is null ? "" : $"Filtered by recorded case classification status: {classificationFilter}. ") +
                (original ? "The original retained case result set was empty." : "The last retained case result set was empty."), 0, incomplete)
                { HistoryValidUntilUtc = snapshot.ExpiresAtUtc };

        var current = await queue.GetSearchableAsync(context.Actor, 20,
            new CaseQueueFilter(CaseIds: snapshot.Cases.Select(item => item.Id).ToArray()), null, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (current.Access != CaseQueueAccess.Allowed || current.NextCursor is not null) return Unavailable();
        if (current.Items.Any(item => item.CaseId == Guid.Empty) ||
            current.Items.Select(item => item.CaseId).Distinct().Count() != current.Items.Count) return Unavailable();
        var authorizedRows = snapshot.Cases.Join(current.Items, old => old.Id, row => row.CaseId,
            (old, row) => (old, row)).ToArray();
        var unknownClassification = classificationFilter is not null &&
            authorizedRows.Any(pair => RecordedClassification(pair.row.Classification) == "unknown");
        var rows = classificationFilter is null ? authorizedRows : authorizedRows.Where(pair =>
        {
            var classification = RecordedClassification(pair.row.Classification);
            return classificationFilter == "Unclassified" ? classification == "Unclassified" :
                classification is not ("Unclassified" or "unknown");
        }).ToArray();
        if (caseId is { } selectedCase)
        {
            if (!snapshot.Cases.Any(item => item.Id == selectedCase)) return Unavailable();
            rows = rows.Where(pair => pair.row.CaseId == selectedCase).ToArray();
            if (rows.Length == 0) return Unavailable();
        }
        var unknownLocations = false;
        if (placeId is not null)
        {
            if (occurrenceLocations is null) return Unavailable();
            var matched = new List<(CaseReference old, CaseQueueItem row)>();
            foreach (var pair in rows)
            {
                ct.ThrowIfCancellationRequested();
                var location = await occurrenceLocations.GetLocationAsync(context.Actor, pair.row.CaseId, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (location.Access == CaseOccurrenceLocationAccess.Unavailable) return Unavailable();
                if (location.Access != CaseOccurrenceLocationAccess.Allowed || location.Location is not { } recorded)
                {
                    unknownLocations = true;
                    continue;
                }
                if (recorded.CountryCode == country &&
                    (recorded.RegionId == placeId || recorded.AreaId == placeId || recorded.LocalityId == placeId))
                    matched.Add(pair);
                else if (recorded.RegionId is null || recorded.AreaId is null || recorded.LocalityId is null)
                    unknownLocations = true;
            }
            rows = matched.ToArray();
        }
        if (field == "observations")
        {
            if (caseReports is null) return Unavailable();
            var reports = new List<(CaseReference Old, CaseReportDetails Report)>();
            foreach (var pair in rows)
            {
                ct.ThrowIfCancellationRequested();
                var report = await caseReports.GetAsync(context.Actor, pair.row.CaseId, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (report is null || report.CaseId != pair.row.CaseId) continue;
                reports.Add((pair.old, report));
            }
            var visibleReports = new List<(CaseReference Old, CaseReportDetails Report)>();
            var outputTruncated = reports.Count != rows.Length;
            foreach (var pair in reports)
            {
                visibleReports.Add(pair);
                if (CreateObservationResult(visibleReports, completeness, classificationFilter, country, placeId,
                    false, incomplete || unknownLocations || reports.Count != rows.Length || authorizedRows.Length != snapshot.Cases.Count)
                    is { } candidate && EllaiToolBroker.MeasureEnvelopeBytes(candidate with { ToolName = EllaiToolNames.Evidence }) > EllaiToolBroker.MaxResultBytes)
                {
                    visibleReports.RemoveAt(visibleReports.Count - 1);
                    outputTruncated = true;
                }
            }
            var result = CreateObservationResult(visibleReports, completeness, classificationFilter, country, placeId,
                outputTruncated, incomplete || unknownLocations || reports.Count != rows.Length || authorizedRows.Length != snapshot.Cases.Count);
            return result with
            { HistoryValidUntilUtc = snapshot.ExpiresAtUtc };
        }
        if (field == "submitter")
        {
            if (caseAttributions is null) return Unavailable();
            var attributions = new List<(Guid CaseId, Guid EventId, long Version, string? DisplayName, bool GuestIntake)>();
            var attributionIncomplete = false;
            foreach (var pair in rows)
            {
                ct.ThrowIfCancellationRequested();
                var attributionResult = await caseAttributions.GetAsync(context.Actor, pair.row.CaseId, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (attributionResult.Access != CaseReportAttributionAccess.Found || attributionResult.Attribution is not { } attribution)
                {
                    attributionIncomplete = true;
                    continue;
                }
                attributions.Add((pair.row.CaseId, attribution.EventId, attribution.AggregateVersion,
                    attribution.ActorDisplayName, attribution.IsPromotedGuestIntake));
            }
            var attributionEvidence = attributions.Select(item => new EvidenceRef("CaseTimeline", item.CaseId.ToString("D"),
                item.Version.ToString(CultureInfo.InvariantCulture), "submitter", item.EventId.ToString("D"))).ToArray();
            var summary = JsonSerializer.Serialize(new
            {
                context = "Recorded case attribution. For promoted guest intake, the managed-case timeline identifies the accepting staff member, not the original guest submitter.",
                cases = attributions.Select(item => new
                {
                    caseId = item.CaseId,
                    submitter = item.GuestIntake ? (string?)null : item.DisplayName,
                    acceptedBy = item.GuestIntake ? item.DisplayName : null,
                    attribution = item.GuestIntake ? "Promoted guest intake; original submitter identity is not available in the managed-case timeline. acceptedBy is the recorded staff member who accepted it." :
                        item.DisplayName is null ? "Recorded submitter name is not available." : "Recorded report submitter."
                }).ToArray()
            });
            return new EllaiToolResult(EllaiToolNames.Topic, true, null, attributionEvidence,
                completeness + "Recorded submitter details from the current retained cases; unavailable records or names are not guessed. " +
                (attributionIncomplete || attributions.Count != rows.Length ? "Some attribution records are incomplete or unavailable. " : "") + summary,
                attributions.Count, incomplete || attributionIncomplete || attributions.Count != rows.Length || authorizedRows.Length != snapshot.Cases.Count)
            { HistoryValidUntilUtc = snapshot.ExpiresAtUtc };
        }
        var evidence = rows.Select(pair => new EvidenceRef("CaseQueue", pair.row.CaseId.ToString("D"),
            pair.row.AggregateVersion.ToString(CultureInfo.InvariantCulture), null, pair.row.CaseId.ToString("D"))).ToArray();
        var evidenceField = classificationFilter is not null ? "classification" : placeId is not null ? "place" : field;
        var scopedEvidence = evidenceField is null
            ? evidence
            : evidence.Select(item => item with { FieldPath = evidenceField }).ToArray();
        var filteredStatus = classificationFilter == "Unclassified" ? "unclassified" : "classified";
        var text = completeness + (original ? "Original retained case results" : "Previously retrieved cases") +
            ", checked against current access; this is not a new search. " +
            "Unavailable records are omitted. " +
            (placeId is null ? "" : $"Filtered to the exact recorded occurrence place {placeId} in {country}. ") +
            (classificationFilter is null ? "" : $"Filtered by recorded case classification status: {classificationFilter}. ") +
            (unknownClassification ? "Cases with unknown classification are excluded. " : "") +
            (unknownLocations ? "Cases with unknown or unavailable occurrence locations are omitted. " : "") +
            "\n" + string.Join("\n", rows.Select(pair =>
            {
                var selectedValue = evidenceField switch
                {
                    "country" => $"country {pair.row.CountryCode}",
                    "workflow" => $"workflow {pair.row.Workflow}",
                    "classification" => $"recorded case classification status {filteredStatus}",
                    "place" => $"recorded occurrence place {placeId}",
                    _ => $"country {pair.row.CountryCode}; workflow {pair.row.Workflow}"
                };
                return $"case {pair.row.CaseId:D}; {selectedValue}; v{pair.row.AggregateVersion}" +
                    (pair.old.Version != pair.row.AggregateVersion ? " (changed since retrieval)" : "");
            }));
        return new EllaiToolResult(EllaiToolNames.Topic, true, null, scopedEvidence, text, rows.Length,
            incomplete || unknownLocations || authorizedRows.Length != snapshot.Cases.Count)
            { HistoryValidUntilUtc = snapshot.ExpiresAtUtc };
    }

    public static string SourceVersion(string snapshot) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshot)));

    private static EllaiToolResult CreateObservationResult(
        IReadOnlyList<(CaseReference Old, CaseReportDetails Report)> reports, string completeness,
        string? classificationFilter, string? country, string? placeId, bool outputTruncated, bool incomplete)
    {
        var evidence = reports.Select(pair => new EvidenceRef("CaseReport", pair.Report.CaseId.ToString("D"),
            pair.Report.ReportRevision.ToString(CultureInfo.InvariantCulture), "observations", pair.Report.CaseId.ToString("D"))).ToArray();
        var observationSummary = JsonSerializer.Serialize(new
        {
            context = "Verbatim observations recorded in reports. These are unverified reports, not diagnoses.",
            cases = reports.Select(pair => new { caseId = pair.Report.CaseId, observations = pair.Report.Observations }).ToArray()
        });
        return new EllaiToolResult(EllaiToolNames.Topic, true, null, evidence, completeness +
            "Reported observations from previously retrieved cases, checked against current access; this is not a new search. " +
            (classificationFilter is null ? "" : $"Filtered by recorded case classification status: {classificationFilter}. ") +
            (placeId is null ? "" : $"Filtered to the exact recorded occurrence place {placeId} in {country}. ") +
            (outputTruncated ? "The authorized output-size limit omitted some case observations. " : "") +
            "Unavailable records are omitted. " + observationSummary, reports.Count, incomplete || outputTruncated);
    }

    private static bool IsValidSnapshot(Snapshot snapshot, bool history) =>
        !string.IsNullOrWhiteSpace(snapshot.ActorId) && !string.IsNullOrWhiteSpace(snapshot.ScopeEpoch) &&
        snapshot.Cases is { Count: <= 20 } && snapshot.Cases.All(item => item is not null && item.Id != Guid.Empty) &&
        snapshot.Cases.Select(item => item.Id).Distinct().Count() == snapshot.Cases.Count &&
        (!history || snapshot.SnapshotId is { Length: 32 } && Guid.TryParseExact(snapshot.SnapshotId, "N", out _) &&
            snapshot.HistoryTruncated is not null && snapshot.Previous is { Count: < MaximumSets } &&
            snapshot.Previous.All(item => item is not null && item.Previous is null && item.HistoryTruncated is null &&
                IsValidSnapshot(item, false) && item.ActorId == snapshot.ActorId && item.ScopeEpoch == snapshot.ScopeEpoch));

    private static EllaiToolResult OriginalUnavailable() => new(EllaiToolNames.Topic, false,
        "The original case result set is no longer available. Run the search again.", [], "", 0, false,
        EllaiToolReasonCodes.Unavailable);

    public static string RecordedClassification(string? value) => value is
        "Unclassified" or "Suspected" or "Probable" or "Confirmed" or "RuledOut" ? value : "unknown";

    private static EllaiToolResult Unavailable() => new(EllaiToolNames.Topic, false,
        "No current, permitted case result set is available to recall. Run the case search again.", [], "", 0, false,
        EllaiToolReasonCodes.Unavailable);
}
