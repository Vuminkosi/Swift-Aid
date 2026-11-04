using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Client.Presentation;

/// <summary>Gateway-backed staff session reads and versioned edits.</summary>
public sealed class StaffSessionEditorHttpClient(HttpClient httpClient) : IStaffSessionEditorClient, IStaffSessionDraftReservationClient
{
    private const string Capability = "community-session-editor";
    private readonly ConcurrentDictionary<Guid, SessionDraftReservation> _reservations = new();

    /// <summary>Obtains an owner-bound identity before constructing a draft command.</summary>
    public async Task<UiCapabilityResult<SessionDraftReservation>> ReserveDraftAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null) return Result<SessionDraftReservation>(UiCapabilityResultCode.Unavailable);
        if (string.IsNullOrWhiteSpace(organizationId) || organizationId.Length > 256)
            return Result<SessionDraftReservation>(UiCapabilityResultCode.InvalidRequest);
        try
        {
            using var response = await httpClient.PostAsJsonAsync("api/v1/session-editor/draft-reservations", new { organizationId }, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Result<SessionDraftReservation>(MapStatus(response.StatusCode));
            var reservation = await response.Content.ReadFromJsonAsync<SessionDraftReservation>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (reservation is null || reservation.SessionId == Guid.Empty || reservation.Token is not { Length: > 0 and <= 4096 })
                return Result<SessionDraftReservation>(UiCapabilityResultCode.Unavailable);
            _reservations[reservation.SessionId] = reservation;
            return Result(UiCapabilityResultCode.Success, reservation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Result<SessionDraftReservation>(UiCapabilityResultCode.Unavailable); }
    }

    public async Task<UiCapabilityResult<string>> GetCurrentActorIdAsync(CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync<string>("api/v1/session-editor/actor", cancellationToken).ConfigureAwait(false);
        return result.Succeeded && string.IsNullOrWhiteSpace(result.Value) ? Result<string>(UiCapabilityResultCode.Unavailable) : result;
    }

    public async Task<UiCapabilityResult<StaffSessionManagementCapabilities>> GetManagementCapabilitiesAsync(Guid? sessionId,
        long? expectedVersion, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty || expectedVersion is <= 0 || sessionId.HasValue != expectedVersion.HasValue)
            return Result<StaffSessionManagementCapabilities>(UiCapabilityResultCode.InvalidRequest);
        var path = "api/v1/session-editor/capabilities" + (sessionId is null ? "" : $"?sessionId={sessionId:D}&version={expectedVersion}");
        var result = await ReadAsync<StaffSessionManagementCapabilities>(path, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Value is not { } caps) return result;
        if (caps.SessionId != sessionId || sessionId is null && caps.Version is not null ||
            sessionId is not null && (caps.Version is null || caps.Version <= 0 || string.IsNullOrWhiteSpace(caps.Status)) ||
            caps.IsCurrent && sessionId is not null && caps.Version != expectedVersion ||
            caps.OwnerOrganizationIds is { Count: > 1000 } ||
            caps.OwnerOrganizationIds?.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256) == true)
            return Result<StaffSessionManagementCapabilities>(UiCapabilityResultCode.Unavailable);
        return result;
    }

    public async Task<UiCapabilityResult<CommunitySessionView>> GetStaffAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) return Result<CommunitySessionView>(UiCapabilityResultCode.InvalidRequest);
        var result = await ReadAsync<CommunitySessionView>($"api/v1/session-editor/sessions/{sessionId:D}", cancellationToken).ConfigureAwait(false);
        return result.Succeeded && (result.Value?.SessionId != sessionId || result.Value.Version <= 0)
            ? Result<CommunitySessionView>(UiCapabilityResultCode.Unavailable) : result;
    }

    public async Task<UiCapabilityResult<SessionCommandResult>> ExecuteAsync(SessionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (httpClient.BaseAddress is null) return Result<SessionCommandResult>(UiCapabilityResultCode.Unavailable);
        if (command is CreateSessionDraftCommand create) return await CreateAsync(create, cancellationToken).ConfigureAwait(false);
        if (command.SessionId == Guid.Empty || command.ExpectedVersion <= 0)
            return Result<SessionCommandResult>(UiCapabilityResultCode.InvalidRequest);
        var path = $"api/v1/sessions/{command.SessionId:D}";
        object? body = command switch
        {
            UpdateSessionCommand update => new { update.ExpectedVersion, update.Title, update.Summary, update.AudienceDescription,
                update.TopicTags, update.Presenter, update.Language, update.StartUtc, update.DurationMinutes, update.Capacity,
                update.Materials, Visibility = update.Visibility.ToString(), ChannelType = update.ChannelType.ToString(),
                update.ChannelLink, update.ReminderLeadHours, update.Reason },
            ReviewSessionCommand review => new { review.ExpectedVersion, review.AcknowledgeSelfReview, review.Reason },
            ScheduleSessionCommand schedule => new { schedule.ExpectedVersion, schedule.Reason },
            CancelSessionCommand cancel => new { cancel.ExpectedVersion, cancel.CancellationReason, cancel.Reason },
            CompleteSessionCommand complete => new { complete.ExpectedVersion, complete.Reason },
            _ => null
        };
        var suffix = command switch
        {
            UpdateSessionCommand => "", ReviewSessionCommand => "/review", ScheduleSessionCommand => "/schedule",
            CancelSessionCommand => "/cancel", CompleteSessionCommand => "/complete", _ => null
        };
        if (body is null || suffix is null) return Result<SessionCommandResult>(UiCapabilityResultCode.Unavailable);
        try
        {
            using var request = new HttpRequestMessage(command is UpdateSessionCommand ? HttpMethod.Put : HttpMethod.Post, path + suffix)
                { Content = JsonContent.Create(body) };
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Result<SessionCommandResult>(MapStatus(response.StatusCode));
            var value = await response.Content.ReadFromJsonAsync<SessionCommandResult>(cancellationToken: cancellationToken).ConfigureAwait(false);
            return value is not null && value.SessionId == command.SessionId && value.Version > command.ExpectedVersion && !string.IsNullOrWhiteSpace(value.Status)
                ? Result(UiCapabilityResultCode.Success, value) : Result<SessionCommandResult>(UiCapabilityResultCode.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Result<SessionCommandResult>(UiCapabilityResultCode.Unavailable); }
    }

    private async Task<UiCapabilityResult<SessionCommandResult>> CreateAsync(CreateSessionDraftCommand command, CancellationToken ct)
    {
        if (command.SessionId == Guid.Empty || !_reservations.TryGetValue(command.SessionId, out var reservation))
            return Result<SessionCommandResult>(UiCapabilityResultCode.Unavailable);
        var draft = new { command.OrganizationId, command.Title, command.Summary, command.AudienceDescription, command.TopicTags,
            command.CountryCode, command.RegionId, command.Sector, command.Presenter, command.Language, command.StartUtc,
            command.TimeZoneId, command.DurationMinutes, command.Capacity, command.Materials, Visibility = command.Visibility.ToString(),
            ChannelType = command.ChannelType.ToString(), command.ChannelLink, command.ReminderLeadHours, command.Reason };
        try
        {
            using var response = await httpClient.PostAsJsonAsync("api/v1/session-editor/drafts",
                new { command.SessionId, ReservationToken = reservation.Token, Draft = draft }, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Result<SessionCommandResult>(MapStatus(response.StatusCode));
            var value = await response.Content.ReadFromJsonAsync<SessionCommandResult>(cancellationToken: ct).ConfigureAwait(false);
            if (value is null || value.SessionId != command.SessionId || value.Version <= 0 || string.IsNullOrWhiteSpace(value.Status))
                return Result<SessionCommandResult>(UiCapabilityResultCode.Unavailable);
            _reservations.TryRemove(command.SessionId, out _);
            return Result(UiCapabilityResultCode.Success, value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Result<SessionCommandResult>(UiCapabilityResultCode.Unavailable); }
    }

    private async Task<UiCapabilityResult<T>> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (httpClient.BaseAddress is null) return Result<T>(UiCapabilityResultCode.Unavailable);
        try
        {
            using var response = await httpClient.GetAsync(path, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Result<T>(MapStatus(response.StatusCode));
            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value is null ? Result<T>(UiCapabilityResultCode.Unavailable) : Result(UiCapabilityResultCode.Success, value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Result<T>(UiCapabilityResultCode.Unavailable); }
    }

    private static UiCapabilityResultCode MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound => UiCapabilityResultCode.Denied,
        HttpStatusCode.Conflict => UiCapabilityResultCode.Conflict,
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => UiCapabilityResultCode.InvalidRequest,
        _ => UiCapabilityResultCode.Unavailable
    };

    private static UiCapabilityResult<T> Result<T>(UiCapabilityResultCode code, T? value = default) =>
        new(code, value, new(Capability, UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow, code is UiCapabilityResultCode.Success or UiCapabilityResultCode.Conflict));
}
