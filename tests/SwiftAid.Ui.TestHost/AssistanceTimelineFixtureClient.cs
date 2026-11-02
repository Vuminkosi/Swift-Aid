using SwiftAid.Contracts.Cases;

namespace SwiftAid.Ui.TestHost;

internal sealed class AssistanceTimelineFixtureClient : ICaseTimelineClient
{
    private static readonly Guid CaseId = Guid.Parse("b2210000-2222-4222-8222-222222222222");
    private static readonly string[] AssistanceEvents =
    [
        "AssistanceRequested",
        "AssistanceAccepted",
        "AssistanceProgressRecorded",
        "AssistanceProvided",
        "AssistanceReceiptConfirmed",
        "AssistanceUnfulfilled",
        "AssistanceCancelled"
    ];

    public Task<CaseTimelineResult> GetAsync(
        Guid caseId,
        int? limit,
        CancellationToken cancellationToken = default,
        long? beforeAggregateVersion = null)
    {
        if (caseId != CaseId || cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null));
        }

        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-AssistanceEvents.Length);
        var entries = AssistanceEvents
            .Select((eventType, index) => new CaseTimelineEntry(
                Guid.Parse($"{index + 1:x8}-0000-4000-8000-000000000001"),
                eventType,
                DisplayLabel(eventType),
                CaseTimelineEntryKind.Authoritative,
                index + 1,
                1,
                Guid.Parse("c5510000-5555-4555-8555-555555555555"),
                "synthetic-actor-id",
                null,
                createdAt.AddMinutes(index),
                1,
                "Synthetic reviewer"))
            .Where(entry => beforeAggregateVersion is null || entry.AggregateVersion < beforeAggregateVersion)
            .Take(limit ?? AssistanceEvents.Length)
            .ToArray();

        var header = new CaseTimelineHeader(CaseId, "synthetic-org", "synthetic-workspace", "SZ",
            "Active", "synthetic-owner", AssistanceEvents.Length, 1, createdAt);
        return Task.FromResult(new CaseTimelineResult(CaseTimelineAccess.Found,
            new CaseTimeline(header, entries, beforeAggregateVersion is not null && beforeAggregateVersion > 1,
                beforeAggregateVersion is null ? null : entries.FirstOrDefault()?.AggregateVersion)));
    }

    private static string DisplayLabel(string eventType) => eventType switch
    {
        "AssistanceRequested" => "Assistance requested",
        "AssistanceAccepted" => "Assistance responsibility accepted",
        "AssistanceProgressRecorded" => "Assistance progress recorded",
        "AssistanceProvided" => "Assistance provided; receipt unconfirmed",
        "AssistanceReceiptConfirmed" => "Assistance receipt confirmed",
        "AssistanceUnfulfilled" => "Assistance not fulfilled",
        "AssistanceCancelled" => "Assistance request cancelled",
        _ => "Case activity recorded"
    };
}
