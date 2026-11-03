namespace SwiftAid.Contracts.Sessions;

/// <summary>Staff command envelope for one versioned community-session change.</summary>
public abstract record SessionCommand(
    Guid SessionId,
    long ExpectedVersion,
    string Reason,
    DateTimeOffset RecordedAtUtc);

/// <summary>Creates a draft. The start may be tentative; scheduling validates it is future.</summary>
public sealed record CreateSessionDraftCommand(
    Guid SessionId,
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
    IReadOnlyList<SessionMaterialRef> Materials,
    SessionVisibility Visibility,
    SessionChannelType ChannelType,
    string? ChannelLink,
    IReadOnlyList<int> ReminderLeadHours,
    string Reason,
    DateTimeOffset RecordedAtUtc) : SessionCommand(SessionId, 0, Reason, RecordedAtUtc);

/// <summary>Records review. A creator publishing their own draft sets AcknowledgeSelfReview.</summary>
public sealed record ReviewSessionCommand(
    Guid SessionId,
    long ExpectedVersion,
    bool AcknowledgeSelfReview,
    string Reason,
    DateTimeOffset RecordedAtUtc) : SessionCommand(SessionId, ExpectedVersion, Reason, RecordedAtUtc);

/// <summary>Publishes a reviewed session. Fails when the start is not future.</summary>
public sealed record ScheduleSessionCommand(
    Guid SessionId,
    long ExpectedVersion,
    string Reason,
    DateTimeOffset RecordedAtUtc) : SessionCommand(SessionId, ExpectedVersion, Reason, RecordedAtUtc);

/// <summary>Edits details. On a scheduled session this is a new published revision (ICS sequence++).</summary>
public sealed record UpdateSessionCommand(
    Guid SessionId,
    long ExpectedVersion,
    string Title,
    string Summary,
    string AudienceDescription,
    IReadOnlyList<string> TopicTags,
    string Presenter,
    string Language,
    DateTimeOffset StartUtc,
    int DurationMinutes,
    int? Capacity,
    IReadOnlyList<SessionMaterialRef> Materials,
    SessionVisibility Visibility,
    SessionChannelType ChannelType,
    string? ChannelLink,
    IReadOnlyList<int> ReminderLeadHours,
    string Reason,
    DateTimeOffset RecordedAtUtc) : SessionCommand(SessionId, ExpectedVersion, Reason, RecordedAtUtc);

/// <summary>Cancels a reviewed or scheduled session and suppresses pending reminders.</summary>
public sealed record CancelSessionCommand(
    Guid SessionId,
    long ExpectedVersion,
    string CancellationReason,
    string Reason,
    DateTimeOffset RecordedAtUtc) : SessionCommand(SessionId, ExpectedVersion, Reason, RecordedAtUtc);

/// <summary>Completes a scheduled session after it starts.</summary>
public sealed record CompleteSessionCommand(
    Guid SessionId,
    long ExpectedVersion,
    string Reason,
    DateTimeOffset RecordedAtUtc) : SessionCommand(SessionId, ExpectedVersion, Reason, RecordedAtUtc);

/// <summary>Opaque source-card reference bound to the session.</summary>
public sealed record SessionMaterialRef(string MaterialId, string SourceVersion);

public enum SessionVisibility
{
    Public,
    Internal
}

public enum SessionChannelType
{
    None,
    FacebookPage,
    WhatsAppGroup,
    Other
}

/// <summary>Material reference with bound version, as stored.</summary>
public sealed record StoredSessionMaterial(string MaterialId, string SourceVersion);

/// <summary>Staff full view. Internal notes stay out of public DTOs.</summary>
public sealed record CommunitySessionView(
    Guid SessionId,
    long Version,
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
    IReadOnlyList<StoredSessionMaterial> Materials,
    SessionVisibility Visibility,
    SessionChannelType ChannelType,
    string? ChannelLink,
    IReadOnlyList<int> ReminderLeadHours,
    string Status,
    string CreatedByActorId,
    string? ReviewedByActorId,
    bool SelfReviewAcknowledged,
    long IcsSequence,
    string? CancellationReason);

/// <summary>Public-safe projection. No attendee data or internal audit fields; material references remain empty until public rights and withdrawal can be verified.</summary>
public sealed record PublicSessionView(
    Guid SessionId,
    long Version,
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
    IReadOnlyList<StoredSessionMaterial> Materials,
    SessionChannelType ChannelType,
    string? ChannelLink,
    string Status,
    string? CancellationReason,
    long IcsSequence);

/// <summary>Session command outcome.</summary>
public sealed record SessionCommandResult(
    Guid SessionId,
    long Version,
    string Status);

/// <summary>A bounded public discovery query. The continuation is a position, not an authorization grant.</summary>
public sealed record PublicSessionQuery(
    string? Search = null,
    string? CountryCode = null,
    string? RegionId = null,
    string? Sector = null,
    string? Language = null,
    string? Topic = null,
    DateTimeOffset? StartFromUtc = null,
    DateTimeOffset? StartBeforeUtc = null,
    DateTimeOffset? AfterStartUtc = null,
    Guid? AfterSessionId = null,
    int Limit = 25,
    string? Category = null,
    DateOnly? LocalDateFrom = null,
    DateOnly? LocalDateTo = null);

/// <summary>Position for the next page of a public session query.</summary>
public sealed record PublicSessionPosition(DateTimeOffset StartUtc, Guid SessionId);

/// <summary>Public-safe sessions in start-time and session-id order.</summary>
public sealed record PublicSessionPage(
    IReadOnlyList<PublicSessionView> Items,
    PublicSessionPosition? Next);

/// <summary>Officer session service. Servers enforce staff authentication; the domain enforces review rules.</summary>
public interface ICommunitySessionService
{
    Task<SessionCommandResult> ExecuteAsync(SessionCommand command, string actorId, CancellationToken ct);

    Task<CommunitySessionView?> GetStaffAsync(Guid sessionId, string actorId, CancellationToken ct);

    Task<IReadOnlyList<PublicSessionView>> ListPublicAsync(int? limit, CancellationToken ct);

    Task<PublicSessionPage> QueryPublicAsync(PublicSessionQuery query, CancellationToken ct);

    Task<PublicSessionView?> GetPublicAsync(Guid sessionId, CancellationToken ct);

    Task<string?> GetIcsAsync(Guid sessionId, DateTimeOffset generatedAtUtc, CancellationToken ct);

    Task<string?> GetInvitationTextAsync(Guid sessionId, CancellationToken ct);
}
