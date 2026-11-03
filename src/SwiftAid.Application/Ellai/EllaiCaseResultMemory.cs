using System.Globalization;
using System.Text.Json;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Session-local result references. PostgreSQL topics hold no case narratives;
/// every recall uses the ordinary owner-filtered, per-case authorized query.</summary>
public sealed class EllaiCaseResultMemory(EllaiTopicService topics, ICaseQueueQuery queue, IEllaiExactReferenceIndex? index = null)
{
    public const string Reference = "case-results";
    public const string TopicTitle = "Ellai latest case result references";
    private sealed record Snapshot(string ActorId, string ScopeEpoch, DateTimeOffset ExpiresAtUtc,
        IReadOnlyList<CaseReference> Cases);
    private sealed record CaseReference(Guid Id, long Version);

    public async Task RememberAsync(EllaiToolContext context, IReadOnlyList<CaseQueueItem> items,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.Actor is null) return;
        var snapshot = new Snapshot(context.Actor.ActorId, context.ScopeEpoch, DateTimeOffset.UtcNow.AddHours(24),
            items.Take(20).Select(item => new CaseReference(item.CaseId, item.AggregateVersion)).ToArray());
        await topics.TrackAsync(context.SessionId, TopicTitle, [Reference],
            JsonSerializer.Serialize(snapshot), "case-results/1", cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<EllaiToolResult> RecallAsync(
        EllaiToolContext context, CancellationToken ct, string? field = null)
    {
        ct.ThrowIfCancellationRequested();
        if (field is not null && field is not ("country" or "workflow"))
            return new(EllaiToolNames.Topic, false, "Unsupported case-result field selector.", [], "", 0, false,
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
            snapshot = topic?.EntityVersion == "case-results/1" && topic.EntityRef is { Length: <= 16000 } json
                ? JsonSerializer.Deserialize<Snapshot>(json) : null;
        }
        catch (JsonException) { return Unavailable(); }
        if (snapshot is null || snapshot.ActorId != context.Actor.ActorId || snapshot.ScopeEpoch != context.ScopeEpoch
            || snapshot.ExpiresAtUtc <= DateTimeOffset.UtcNow || snapshot.Cases is null || snapshot.Cases.Count > 20
            || snapshot.Cases.Any(item => item is null || item.Id == Guid.Empty))
            return Unavailable();
        if (snapshot.Cases.Count == 0)
            return new EllaiToolResult(EllaiToolNames.Topic, true, null, [], "The last retained case result set was empty.", 0, false)
                { HistoryValidUntilUtc = snapshot.ExpiresAtUtc };

        var current = await queue.GetMineAsync(context.Actor, 20,
            new CaseQueueFilter(CaseIds: snapshot.Cases.Select(item => item.Id).ToArray()), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (current.Access != CaseQueueAccess.Allowed) return Unavailable();
        var rows = snapshot.Cases.Join(current.Items, old => old.Id, row => row.CaseId,
            (old, row) => (old, row)).ToArray();
        var evidence = rows.Select(pair => new EvidenceRef("CaseQueue", pair.row.CaseId.ToString("D"),
            pair.row.AggregateVersion.ToString(CultureInfo.InvariantCulture), null, pair.row.CaseId.ToString("D"))).ToArray();
        var scopedEvidence = field is null
            ? evidence
            : evidence.Select(item => item with { FieldPath = field }).ToArray();
        var text = "Previously retrieved cases, checked against current access; this is not a new search. " +
            "Unavailable records are omitted.\n" + string.Join("\n", rows.Select(pair =>
            {
                var selectedValue = field switch
                {
                    "country" => $"country {pair.row.CountryCode}",
                    "workflow" => $"workflow {pair.row.Workflow}",
                    _ => $"country {pair.row.CountryCode}; workflow {pair.row.Workflow}"
                };
                return $"case {pair.row.CaseId:D}; {selectedValue}; v{pair.row.AggregateVersion}" +
                    (pair.old.Version != pair.row.AggregateVersion ? " (changed since retrieval)" : "");
            }));
        return new EllaiToolResult(EllaiToolNames.Topic, true, null, scopedEvidence, text, rows.Length, false)
            { HistoryValidUntilUtc = snapshot.ExpiresAtUtc };
    }

    public static string SourceVersion(string snapshot) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshot)));

    private static EllaiToolResult Unavailable() => new(EllaiToolNames.Topic, false,
        "No current, permitted case result set is available to recall. Run the case search again.", [], "", 0, false,
        EllaiToolReasonCodes.Unavailable);
}
