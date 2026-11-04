namespace SwiftAid.Application.CommunityResearch;

/// <summary>Owner-attested aggregate of session feedback; no individual response is stored here.</summary>
public sealed record CommunityFeedbackAggregate(
    [property: System.Text.Json.Serialization.JsonRequired] Guid AggregateId,
    [property: System.Text.Json.Serialization.JsonRequired] Guid CommandId,
    [property: System.Text.Json.Serialization.JsonRequired] string OrganizationId,
    [property: System.Text.Json.Serialization.JsonRequired] Guid SessionId,
    [property: System.Text.Json.Serialization.JsonRequired] long SessionVersion,
    [property: System.Text.Json.Serialization.JsonRequired] string TopicCode,
    [property: System.Text.Json.Serialization.JsonRequired] DateTimeOffset WindowStartUtc,
    [property: System.Text.Json.Serialization.JsonRequired] DateTimeOffset WindowEndUtc,
    [property: System.Text.Json.Serialization.JsonRequired] int RespondentCount,
    [property: System.Text.Json.Serialization.JsonRequired] int MentionCount);

public static class CommunityFeedbackAggregateRules
{
    public const int MinimumCellSize = 5;
    public const int MinimumRespondents = 10;
    public const int MaximumRespondents = 1_000_000;

    public static bool IsValid(CommunityFeedbackAggregate item, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.AggregateId == Guid.Empty || item.CommandId == Guid.Empty || item.SessionId == Guid.Empty || item.SessionVersion <= 0 ||
            string.IsNullOrWhiteSpace(item.OrganizationId) || item.OrganizationId.Length > 120 || item.OrganizationId != item.OrganizationId.Trim() ||
            string.IsNullOrEmpty(item.TopicCode) || item.TopicCode.Length > 64 || item.TopicCode[0] is < 'a' or > 'z' ||
            item.TopicCode.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) ||
            nowUtc.Offset != TimeSpan.Zero || item.WindowStartUtc.Offset != TimeSpan.Zero || item.WindowEndUtc.Offset != TimeSpan.Zero ||
            item.WindowStartUtc < nowUtc.AddDays(-30) || item.WindowEndUtc > nowUtc || item.WindowEndUtc <= item.WindowStartUtc ||
            item.RespondentCount is < MinimumRespondents or > MaximumRespondents || item.MentionCount < 0 || item.MentionCount > item.RespondentCount)
            return false;
        var complement = item.RespondentCount - item.MentionCount;
        return (item.MentionCount == 0 || item.MentionCount >= MinimumCellSize) &&
            (complement == 0 || complement >= MinimumCellSize);
    }
}
