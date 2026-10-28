using SwiftAid.Contracts.Sessions;

namespace SwiftAid.UI.Components.Sessions;

/// <summary>Officer-supplied material reference for the session editor.</summary>
public sealed record SessionMaterialInput(string MaterialId, string SourceVersion);

/// <summary>Officer-supplied fields for creating a session draft.</summary>
public sealed record CreateSessionInput(
    string Title,
    string Summary,
    string AudienceDescription,
    IReadOnlyList<string> TopicTags,
    string CountryCode,
    string RegionId,
    string Sector,
    string Presenter,
    string Language,
    DateTimeOffset StartUtc,
    string TimeZoneId,
    int DurationMinutes,
    int? Capacity,
    IReadOnlyList<SessionMaterialInput> Materials,
    SessionVisibility Visibility,
    SessionChannelType ChannelType,
    string? ChannelLink,
    IReadOnlyList<int> ReminderLeadHours)
{
    /// <summary>Builds the creation command; the gateway page supplies the fresh identifier.</summary>
    public CreateSessionDraftCommand ToCommand(Guid sessionId, string reason, DateTimeOffset recordedAtUtc) =>
        new(
            sessionId,
            Title,
            Summary,
            AudienceDescription,
            TopicTags,
            CountryCode,
            RegionId,
            Sector,
            Presenter,
            Language,
            StartUtc,
            TimeZoneId,
            DurationMinutes,
            Capacity,
            Materials.Select(m => new SessionMaterialRef(m.MaterialId, m.SourceVersion)).ToArray(),
            Visibility,
            ChannelType,
            ChannelLink,
            ReminderLeadHours,
            reason,
            recordedAtUtc);
}

/// <summary>Officer-supplied fields for editing a draft, reviewed, or scheduled session.</summary>
public sealed record UpdateSessionInput(
    string Title,
    string Summary,
    string AudienceDescription,
    IReadOnlyList<string> TopicTags,
    string Presenter,
    string Language,
    DateTimeOffset StartUtc,
    int DurationMinutes,
    int? Capacity,
    IReadOnlyList<SessionMaterialInput> Materials,
    SessionVisibility Visibility,
    SessionChannelType ChannelType,
    string? ChannelLink,
    IReadOnlyList<int> ReminderLeadHours)
{
    /// <summary>Builds the update command for the known session version.</summary>
    public UpdateSessionCommand ToCommand(Guid sessionId, long expectedVersion, string reason, DateTimeOffset recordedAtUtc) =>
        new(
            sessionId,
            expectedVersion,
            Title,
            Summary,
            AudienceDescription,
            TopicTags,
            Presenter,
            Language,
            StartUtc,
            DurationMinutes,
            Capacity,
            Materials.Select(m => new SessionMaterialRef(m.MaterialId, m.SourceVersion)).ToArray(),
            Visibility,
            ChannelType,
            ChannelLink,
            ReminderLeadHours,
            reason,
            recordedAtUtc);
}

/// <summary>Editor submission for creating a draft, with the recorded reason.</summary>
public sealed record SessionCreateSubmission(CreateSessionInput Input, string Reason);

/// <summary>Editor submission for updating a known session version, with the recorded reason.</summary>
public sealed record SessionUpdateSubmission(Guid SessionId, long ExpectedVersion, UpdateSessionInput Input, string Reason);

/// <summary>Editor submission for recording review, with the recorded reason.</summary>
public sealed record SessionReviewSubmission(bool AcknowledgeSelfReview, string Reason);

/// <summary>Editor submission for cancelling a session, with the recorded reasons.</summary>
public sealed record SessionCancelSubmission(string CancellationReason, string Reason);

/// <summary>Prefills editor inputs from the staff session view.</summary>
public static class SessionEditorPrefill
{
    public static CreateSessionInput FromStaffView(CommunitySessionView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new CreateSessionInput(
            view.Title,
            view.Summary,
            view.AudienceDescription,
            view.TopicTags,
            view.CountryCode,
            view.RegionId,
            view.Sector,
            view.Presenter,
            view.Language,
            view.StartUtc,
            view.TimeZoneId,
            view.DurationMinutes,
            view.Capacity,
            view.Materials.Select(m => new SessionMaterialInput(m.MaterialId, m.SourceVersion)).ToArray(),
            view.Visibility,
            view.ChannelType,
            view.ChannelLink,
            view.ReminderLeadHours);
    }
}
