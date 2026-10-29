using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Client.Sessions;

/// <summary>Calendar file downloaded from the public session endpoints.</summary>
public sealed record SessionCalendarFile(string FileName, string ContentType, byte[] Content);

/// <summary>Public community-session client over the anonymous Gateway API.</summary>
/// <remarks>
/// Published sessions only: the endpoints return the public projection, and a missing
/// or unpublished session is a null result, never an exception. Sharing stays manual:
/// this client only reads the invitation text and calendar file for the caller to copy.
/// </remarks>
public sealed class CommunitySessionHttpClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Typed public list result used by presentation clients that need truthful availability state.</summary>
    public async Task<UiCapabilityResult<IReadOnlyList<PublicSessionView>>> ListPublicResultAsync(
        int? limit, CancellationToken cancellationToken = default)
    {
        if (limit is <= 0 or > 100)
            return new UiCapabilityResult<IReadOnlyList<PublicSessionView>>(
                UiCapabilityResultCode.InvalidRequest, null, LiveMetadata("public-community-sessions"),
                "Choose a list limit from 1 through 100.");
        if (httpClient.BaseAddress is null)
            return UiCapabilityResults.Unavailable<IReadOnlyList<PublicSessionView>>("public-community-sessions");
        var address = "public/v1/sessions";
        if (limit.HasValue) address += $"?limit={limit.Value}";
        try
        {
            using var response = await httpClient.GetAsync(address, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return UiCapabilityResults.Unavailable<IReadOnlyList<PublicSessionView>>("public-community-sessions");
            var sessions = await response.Content.ReadFromJsonAsync<IReadOnlyList<PublicSessionView>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return Success<IReadOnlyList<PublicSessionView>>(sessions ?? [], "public-community-sessions");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UiCapabilityResults.Unavailable<IReadOnlyList<PublicSessionView>>("public-community-sessions");
        }
    }

    /// <summary>Typed invitation-text result; missing and unavailable are not reported as successful empty content.</summary>
    public async Task<UiCapabilityResult<string>> GetInvitationTextResultAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return new UiCapabilityResult<string>(UiCapabilityResultCode.InvalidRequest, null,
                LiveMetadata("public-community-session-invitation"), "Choose a valid session.");
        if (httpClient.BaseAddress is null)
            return UiCapabilityResults.Unavailable<string>("public-community-session-invitation");
        try
        {
            using var response = await httpClient.GetAsync($"public/v1/sessions/{sessionId:D}/invitation", cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return UiCapabilityResults.Unavailable<string>("public-community-session-invitation");
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Success(text, "public-community-session-invitation");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UiCapabilityResults.Unavailable<string>("public-community-session-invitation");
        }
    }

    /// <summary>Lists published public sessions; empty when unavailable.</summary>
    public async Task<IReadOnlyList<PublicSessionView>> ListPublicAsync(
        int? limit, CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null)
        {
            return [];
        }

        var address = "public/v1/sessions";
        if (limit.HasValue)
        {
            address += $"?limit={limit.Value}";
        }

        try
        {
            using var response = await httpClient.GetAsync(address, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return [];
            }

            var sessions = await response.Content
                .ReadFromJsonAsync<IReadOnlyList<PublicSessionView>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return sessions ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Reads one published session; null when missing, unpublished, or unavailable.</summary>
    public async Task<PublicSessionView?> GetPublicAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null)
        {
            return null;
        }

        try
        {
            using var response = await httpClient.GetAsync(
                $"public/v1/sessions/{sessionId:D}", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<PublicSessionView>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads the plain-text invitation logistics; null when unavailable.</summary>
    public async Task<string?> GetInvitationTextAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null)
        {
            return null;
        }

        try
        {
            using var response = await httpClient.GetAsync(
                $"public/v1/sessions/{sessionId:D}/invitation", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Downloads the calendar file; null when missing or unavailable.</summary>
    public async Task<SessionCalendarFile?> GetCalendarFileAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null)
        {
            return null;
        }

        try
        {
            using var response = await httpClient.GetAsync(
                $"public/v1/sessions/{sessionId:D}/calendar", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "text/calendar";
            return new SessionCalendarFile($"session-{sessionId:D}.ics", contentType, content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static UiCapabilityResult<T> Success<T>(T value, string capability) => new(
        UiCapabilityResultCode.Success,
        value,
        LiveMetadata(capability));

    private static UiCapabilityMetadata LiveMetadata(string capability) => new(
        capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        IsAvailable: true);
}
