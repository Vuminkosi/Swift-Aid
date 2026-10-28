using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Client.Coordination;

/// <summary>Staff coordination queue client over the authenticated Gateway API.</summary>
/// <remarks>
/// Reads and acts on unrouted intake reports only. The API returns routing fields;
/// narratives, precise coordinates, and original audio are never in these payloads,
/// so this client cannot display or persist them.
/// </remarks>
public sealed class IntakeCoordinationHttpClient(HttpClient httpClient) : IIntakeCoordinationClient
{
    private const int QueuePageLimit = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(CoordinationQueueAccess Access, IReadOnlyList<IntakeCoordinationQueueEntry> Entries)> GetQueueAsync(
        IntakeRoutingStatus? routingStatus,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null || limit < 1)
        {
            return (CoordinationQueueAccess.Unavailable, []);
        }

        var path = $"api/v1/intake/coordination/queue?limit={Math.Min(limit, QueuePageLimit)}";
        if (routingStatus is not null)
        {
            path += $"&routingStatus={routingStatus.Value}";
        }

        using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized)
        {
            return (CoordinationQueueAccess.Denied, []);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return (CoordinationQueueAccess.Denied, []);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (CoordinationQueueAccess.Unavailable, []);
        }

        try
        {
            var entries = await response.Content
                .ReadFromJsonAsync<List<IntakeCoordinationQueueEntry>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return (CoordinationQueueAccess.Allowed, entries ?? []);
        }
        catch (JsonException)
        {
            return (CoordinationQueueAccess.Unavailable, []);
        }
    }

    public async Task<IntakeCoordinationDetail?> GetDetailAsync(
        Guid receiptId,
        CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null || receiptId == Guid.Empty)
        {
            return null;
        }

        using var response = await httpClient.GetAsync(
            $"api/v1/intake/coordination/{receiptId:D}", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            return await response.Content
                .ReadFromJsonAsync<IntakeCoordinationDetail>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<IntakeCoordinationQueueOption>> GetQueuesAsync(
        CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null)
        {
            return [];
        }

        using var response = await httpClient.GetAsync(
            "api/v1/intake/coordination/queues", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        try
        {
            return await response.Content
                .ReadFromJsonAsync<List<IntakeCoordinationQueueOption>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<CoordinationActionOutcome> ActAsync(
        Guid receiptId,
        long expectedVersion,
        IntakeCoordinationAction action,
        string? targetQueueId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null || receiptId == Guid.Empty ||
            expectedVersion < 1 || string.IsNullOrWhiteSpace(reason))
        {
            return CoordinationActionOutcome.Invalid;
        }

        using var response = await httpClient.PostAsJsonAsync(
            $"api/v1/intake/coordination/{receiptId:D}/actions",
            new IntakeCoordinationActionRequest(1, expectedVersion, action.ToString(), targetQueueId, reason),
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        return response.StatusCode switch
        {
            HttpStatusCode.OK => CoordinationActionOutcome.Applied,
            HttpStatusCode.Conflict => CoordinationActionOutcome.Conflict,
            HttpStatusCode.BadRequest => CoordinationActionOutcome.Invalid,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
                CoordinationActionOutcome.NotFound,
            _ => CoordinationActionOutcome.Unavailable
        };
    }
}
