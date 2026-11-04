using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Mobile.Services.Sessions;

namespace SwiftAid.Ui.TestHost;

public sealed class StaffSessionManagerFixture(SessionHistoryFixture authentication) : IStaffSessionEditorClient,
    IStaffSessionDraftReservationClient, IPublicCommunitySessionClient, IMobileSessionPlatform
{
    private CommunitySessionView? _session;
    public bool LoseNextCreate { get; set; }
    public bool ConflictNextUpdate { get; set; }
    public bool PauseNextCreate { get; set; }
    private TaskCompletionSource? _pendingCreate;
    public void ReleaseCreate() => _pendingCreate?.TrySetResult();
    public int Creates { get; private set; }
    public bool FailPublicList { get; set; }
    public int PublicListCalls { get; private set; }
    public int PublicQueryCalls { get; private set; }
    private string Actor => authentication.Allowed ? "fixture-owner" : "other-owner";

    public Task<UiCapabilityResult<string>> GetCurrentActorIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(authentication.SignedIn ? Success(Actor) : Denied<string>());

    public Task<UiCapabilityResult<StaffSessionManagementCapabilities>> GetManagementCapabilitiesAsync(Guid? sessionId,
        long? expectedVersion, CancellationToken cancellationToken = default)
    {
        if (!authentication.SignedIn) return Task.FromResult(Denied<StaffSessionManagementCapabilities>());
        if (sessionId is null) return Task.FromResult(Success(new StaffSessionManagementCapabilities(true, false, false, false, false, false,
            null, null, null, true, OwnerOrganizationIds: ["fixture-org"])));
        if (_session is null || _session.SessionId != sessionId || _session.CreatedByActorId != Actor)
            return Task.FromResult(Denied<StaffSessionManagementCapabilities>());
        var current = expectedVersion == _session.Version;
        return Task.FromResult(Success(new StaffSessionManagementCapabilities(false, current, current && _session.Status == "Draft",
            current && _session.Status == "Reviewed", current && _session.Status is "Reviewed" or "Scheduled", false,
            sessionId, _session.Version, _session.Status, current, CanViewHistory: true, OwnerOrganizationIds: ["fixture-org"])));
    }

    public Task<UiCapabilityResult<CommunitySessionView>> GetStaffAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(authentication.SignedIn && _session?.SessionId == sessionId && _session.CreatedByActorId == Actor
            ? Success(_session) : Denied<CommunitySessionView>());

    public Task<UiCapabilityResult<SessionDraftReservation>> ReserveDraftAsync(string organizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(authentication.SignedIn && organizationId == "fixture-org"
            ? Success(new SessionDraftReservation(Guid.NewGuid(), "synthetic-ticket")) : Denied<SessionDraftReservation>());

    public async Task<UiCapabilityResult<SessionCommandResult>> ExecuteAsync(SessionCommand command, CancellationToken cancellationToken = default)
    {
        if (!authentication.SignedIn) return Denied<SessionCommandResult>();
        if (command is CreateSessionDraftCommand create)
        {
            Creates++;
            _session = new(create.SessionId, create.OrganizationId, 1, create.Title, create.Summary, create.AudienceDescription,
                create.TopicTags, create.CountryCode, create.RegionId, create.Sector, create.Presenter, create.Language,
                create.StartUtc, create.TimeZoneId, create.DurationMinutes, create.Capacity, create.Materials, create.Visibility,
                create.ChannelType, create.ChannelLink, create.ReminderLeadHours, "Draft", Actor, null, false, 0, null);
            if (PauseNextCreate)
            {
                PauseNextCreate = false;
                _pendingCreate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await _pendingCreate.Task.WaitAsync(cancellationToken);
                _pendingCreate = null;
            }
            if (LoseNextCreate) { LoseNextCreate = false; return UiCapabilityResults.Unavailable<SessionCommandResult>("fixture"); }
            return Success(new SessionCommandResult(create.SessionId, 1, "Draft"));
        }
        if (_session?.CreatedByActorId != Actor) return Denied<SessionCommandResult>();
        if (command is UpdateSessionCommand update && ConflictNextUpdate && _session?.SessionId == update.SessionId)
        {
            ConflictNextUpdate = false;
            _session = _session with { Version = _session.Version + 1, Title = "Server revision" };
            return new UiCapabilityResult<SessionCommandResult>(UiCapabilityResultCode.Conflict, null, Metadata(true));
        }
        if (command is ReviewSessionCommand review && _session?.SessionId == review.SessionId &&
            _session.Version == review.ExpectedVersion && _session.Status == "Draft" && review.AcknowledgeSelfReview)
        {
            _session = _session with { Version = _session.Version + 1, Status = "Reviewed", ReviewedByActorId = Actor, SelfReviewAcknowledged = true };
            return Success(new SessionCommandResult(_session.SessionId, _session.Version, _session.Status));
        }
        if (command is ScheduleSessionCommand schedule && _session?.SessionId == schedule.SessionId &&
            _session.Version == schedule.ExpectedVersion && _session.Status == "Reviewed")
        {
            _session = _session with { Version = _session.Version + 1, Status = "Scheduled" };
            return Success(new SessionCommandResult(_session.SessionId, _session.Version, _session.Status));
        }
        return UiCapabilityResults.Unavailable<SessionCommandResult>("fixture");
    }

    public Task<UiCapabilityResult<IReadOnlyList<PublicSessionView>>> ListPublicAsync(int? limit = null, CancellationToken cancellationToken = default)
    {
        PublicListCalls++;
        return Task.FromResult(FailPublicList ? UiCapabilityResults.Unavailable<IReadOnlyList<PublicSessionView>>("fixture") : Success<IReadOnlyList<PublicSessionView>>([]));
    }
    public Task<UiCapabilityResult<PublicSessionPage>> QueryPublicAsync(PublicSessionQuery query, CancellationToken cancellationToken = default)
    {
        PublicQueryCalls++;
        return Task.FromResult(Success(new PublicSessionPage([], null)));
    }
    public Task<UiCapabilityResult<PublicSessionView>> GetPublicAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Denied<PublicSessionView>());
    public Task<UiCapabilityResult<string>> GetInvitationTextAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Denied<string>());
    public Task ShareAsync(string text) => Task.CompletedTask;
    public Task<bool> OpenCalendarAsync(Guid sessionId) => Task.FromResult(false);

    private static UiCapabilityResult<T> Success<T>(T value) => new(UiCapabilityResultCode.Success, value, Metadata(true));
    private static UiCapabilityResult<T> Denied<T>() => new(UiCapabilityResultCode.Denied, default, Metadata(false));
    private static UiCapabilityMetadata Metadata(bool available) => new("fixture", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, available);
}
