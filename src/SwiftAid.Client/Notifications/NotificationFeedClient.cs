using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SwiftAid.Contracts.Notifications;

namespace SwiftAid.Client.Notifications;

/// <summary>Calls the authenticated staff-only notification API.</summary>
public sealed class NotificationFeedClient(HttpClient httpClient) : INotificationFeedClient
{
    private const string FeedPath = "api/v1/notifications";
    private const string StreamPath = "api/v1/notifications/stream";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<NotificationFeedResult> GetAsync(CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null)
        {
            return NotificationFeedResult.Unavailable();
        }

        using var response = await httpClient.GetAsync(FeedPath, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return NotificationFeedResult.PermissionDenied();
        }

        if (!response.IsSuccessStatusCode)
        {
            return NotificationFeedResult.Unavailable();
        }

        try
        {
            var page = await response.Content
                .ReadFromJsonAsync<NotificationFeedPageResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return NotificationFeedResult.Ready(page?.Items ?? []);
        }
        catch (JsonException)
        {
            return NotificationFeedResult.Unavailable();
        }
    }

    public Task<bool> MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default) =>
        SendItemActionAsync(notificationId, "read", cancellationToken);

    public Task<bool> DismissAsync(Guid notificationId, CancellationToken cancellationToken = default) =>
        SendItemActionAsync(notificationId, "dismiss", cancellationToken);

    public async Task<PushDeviceRegistrationResult> RegisterDeviceAsync(
        Guid installationId,
        PushDevicePlatform platform,
        string token,
        bool soundEnabled,
        CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null)
        {
            return new PushDeviceRegistrationResult(PushDeviceRegistrationStatus.Unavailable);
        }

        if (installationId == Guid.Empty ||
            !Enum.IsDefined(platform) ||
            string.IsNullOrWhiteSpace(token) ||
            token.Length > 4096 ||
            !string.Equals(token, token.Trim(), StringComparison.Ordinal))
        {
            return new PushDeviceRegistrationResult(PushDeviceRegistrationStatus.Invalid);
        }

        using var response = await httpClient.PostAsJsonAsync(
            "api/v1/push/devices",
            new PushDeviceRegistrationRequest(installationId, platform, token, soundEnabled),
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        var status = response.StatusCode switch
        {
            HttpStatusCode.NoContent => PushDeviceRegistrationStatus.Registered,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => PushDeviceRegistrationStatus.NotAuthenticated,
            HttpStatusCode.BadRequest => PushDeviceRegistrationStatus.Invalid,
            _ => PushDeviceRegistrationStatus.Unavailable
        };

        return new PushDeviceRegistrationResult(status);
    }

    public async Task<PushDeviceRemovalResult> RemoveDeviceAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null)
        {
            return new PushDeviceRemovalResult(PushDeviceRemovalStatus.Unavailable);
        }

        if (installationId == Guid.Empty)
        {
            return new PushDeviceRemovalResult(PushDeviceRemovalStatus.NotFoundOrNotAuthorized);
        }

        using var response = await httpClient.DeleteAsync(
            $"api/v1/push/devices/{installationId:D}",
            cancellationToken).ConfigureAwait(false);

        var status = response.StatusCode switch
        {
            HttpStatusCode.NoContent => PushDeviceRemovalStatus.Removed,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => PushDeviceRemovalStatus.NotAuthenticated,
            HttpStatusCode.NotFound => PushDeviceRemovalStatus.NotFoundOrNotAuthorized,
            _ => PushDeviceRemovalStatus.Unavailable
        };

        return new PushDeviceRemovalResult(status);
    }

    public async IAsyncEnumerable<bool> WatchChangesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null)
        {
            yield break;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, StreamPath);
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            yield break;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var changed = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            // Never expose or parse notification IDs, resource references, or payload content
            // from the stream. Any event boundary is only a hint to refetch the authorized feed.
            if (line.Length == 0)
            {
                if (changed)
                {
                    yield return true;
                    changed = false;
                }

                continue;
            }

            if (line.StartsWith("event: changed", StringComparison.Ordinal) ||
                line.StartsWith("event: heartbeat", StringComparison.Ordinal) ||
                line.StartsWith("data:", StringComparison.Ordinal))
            {
                changed = true;
            }
        }

        if (changed && !cancellationToken.IsCancellationRequested)
        {
            yield return true;
        }
    }

    /// <summary>
    /// Keeps the feed fresh from opaque SSE hints. On disconnect or stream failure,
    /// it polls the recipient-scoped HTTP feed while periodically reconnecting.
    /// </summary>
    public async Task MonitorChangesAsync(
        Func<CancellationToken, Task> refreshAuthorizedFeedAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshAuthorizedFeedAsync);

        while (!cancellationToken.IsCancellationRequested)
        {
            var streamEnded = false;
            try
            {
                await foreach (var _ in WatchChangesAsync(cancellationToken).ConfigureAwait(false))
                {
                    await refreshAuthorizedFeedAsync(cancellationToken).ConfigureAwait(false);
                }

                streamEnded = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // A client-side stream timeout should fall back to polling and reconnecting.
            }
            catch (HttpRequestException)
            {
                // Fall back to recipient-scoped polling and retry SSE on the next cycle.
            }
            catch (IOException)
            {
                // The stream was interrupted; do not surface transport details to users.
            }

            if (streamEnded || !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                await refreshAuthorizedFeedAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> SendItemActionAsync(
        Guid notificationId,
        string action,
        CancellationToken cancellationToken)
    {
        if (httpClient.BaseAddress is null || notificationId == Guid.Empty)
        {
            return false;
        }

        using var response = await httpClient.PostAsync(
            $"{FeedPath}/{notificationId:D}/{action}",
            content: null,
            cancellationToken).ConfigureAwait(false);

        return response.IsSuccessStatusCode;
    }

    private sealed record NotificationFeedPageResponse(
        IReadOnlyList<NotificationItemView> Items,
        string? ContinuationToken);
}
