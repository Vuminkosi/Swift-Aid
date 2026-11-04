namespace SwiftAid.UI.Components.Sessions;

/// <summary>In-memory form fields, including incomplete entries, for restoring the same editor after access is rechecked.</summary>
public sealed record SessionEditorWorkingCopy(
    string? ActorId, Guid? SessionId, long ExpectedVersion, string Title, string? OrganizationId, string Summary, string Audience, string Country, string Region, string Sector,
    string Presenter, string Language, string TopicTags, string TimeZone, DateTime? StartLocal, int? AmbiguousOffsetMinutes,
    string Duration, string Capacity, string Visibility, string ChannelType, string? ChannelLink, string ReminderLeads,
    IReadOnlyList<SessionMaterialInput> Materials, string Reason, string? CancellationReason, bool AcknowledgeSelfReview,
    bool StoredTimeZoneUnavailable, IReadOnlyList<string?>? SavedFields)
{
    public override string ToString() => nameof(SessionEditorWorkingCopy);
}
