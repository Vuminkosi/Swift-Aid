using System.Globalization;
using System.Text.Json;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Session-local result references. PostgreSQL topics hold no case narratives;
/// every recall uses the ordinary owner-filtered, per-case authorized query.</summary>
public sealed class EllaiCaseResultMemory(EllaiTopicService topics, ICaseQueueQuery queue)
{
    public const string Reference = "case-results";
    public const string TopicTitle = "Ellai latest case result references";
    private sealed record Snapshot(string ActorId, string ScopeEpoch, DateTimeOffset ExpiresAtUtc,
        IReadOnlyList<CaseReference> Cases);
    private sealed record CaseReference(Guid Id, long Version);

    public async Task RememberAsync(EllaiToolContext context, IReadOnlyList<CaseQueueItem> items,
        CancellationToken ct)
    {
        if (context.Actor is null) return;
        var snapshot = new Snapshot(context.Actor.ActorId, context.ScopeEpoch, DateTimeOffset.UtcNow.AddHours(24),
            items.Take(20).Select(item => new CaseReference(item.CaseId, item.AggregateVersion)).ToArray());
        await topics.TrackAsync(context.SessionId, TopicTitle, [Reference],
            JsonSerializer.Serialize(snapshot), "case-results/1", cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<EllaiToolResult> RecallAsync(EllaiToolContext context, CancellationToken ct)
    {
        if (context.Actor is null) return Unavailable();
        var (matches, _) = await topics.ResolveAsync(context.SessionId, Reference, ct).ConfigureAwait(false);
        var topic = matches.SingleOrDefault(item => item.Title == TopicTitle);
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
            return new(EllaiToolNames.Topic, true, null, [], "The last retained case result set was empty.", 0, false);

        var current = await queue.GetMineAsync(context.Actor, 20,
            new CaseQueueFilter(CaseIds: snapshot.Cases.Select(item => item.Id).ToArray()), ct).ConfigureAwait(false);
        if (current.Access != CaseQueueAccess.Allowed) return Unavailable();
        var rows = snapshot.Cases.Join(current.Items, old => old.Id, row => row.CaseId,
            (old, row) => (old, row)).ToArray();
        var evidence = rows.Select(pair => new EvidenceRef("CaseQueue", pair.row.CaseId.ToString("D"),
            pair.row.AggregateVersion.ToString(CultureInfo.InvariantCulture), null, pair.row.CaseId.ToString("D"))).ToArray();
        var text = "Previously retrieved cases, checked against current access; this is not a new search. " +
            "Unavailable records are omitted.\n" + string.Join("\n", rows.Select(pair =>
                $"case {pair.row.CaseId:D}; country {pair.row.CountryCode}; workflow {pair.row.Workflow}; " +
                $"v{pair.row.AggregateVersion}" + (pair.old.Version != pair.row.AggregateVersion ? " (changed since retrieval)" : "")));
        return new(EllaiToolNames.Topic, true, null, evidence, text, rows.Length, false);
    }

    private static EllaiToolResult Unavailable() => new(EllaiToolNames.Topic, false,
        "No current, permitted case result set is available to recall. Run the case search again.", [], "", 0, false,
        EllaiToolReasonCodes.Unavailable);
}
