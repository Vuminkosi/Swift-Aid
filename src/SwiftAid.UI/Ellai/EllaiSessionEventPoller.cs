using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai;

/// <summary>HTTP review client for the gateway Ellai endpoints: event
/// long-poll, active-review fetch, approve-selected and reject. Relative
/// URLs: the host app supplies the authorized HttpClient. Non-success
/// responses read as unavailable so the caller keeps polling.</summary>
public sealed class EllaiReviewHttpClient(HttpClient http) : IEllaiReviewClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<EllaiSessionEventPageDto?> PollEventsAsync(
        string sessionId, long cursor, int waitSeconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var url = $"api/v1/ellai/sessions/{Uri.EscapeDataString(sessionId)}/events" +
            $"?cursor={Math.Max(0, cursor)}&waitSeconds={Math.Clamp(waitSeconds, 0, 30)}";
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<EllaiSessionEventPageDto>(Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<EllaiReviewDto?> GetActiveReviewAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        try
        {
            using var response = await _http.GetAsync(
                $"api/v1/ellai/sessions/{Uri.EscapeDataString(sessionId)}/approval-reviews/active",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<EllaiReviewDto>(Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ApproveSelectedAsync(
        string sessionId, string requestId, IReadOnlyList<string> actionIds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"api/v1/ellai/sessions/{Uri.EscapeDataString(sessionId)}/approval-reviews/{Uri.EscapeDataString(requestId)}/approve",
                new { sessionId, actionIds }, Json, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<IReadOnlyList<EllaiApprovedReceiptDto>>(Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> RejectReviewAsync(
        string sessionId, string requestId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"api/v1/ellai/sessions/{Uri.EscapeDataString(sessionId)}/approval-reviews/{Uri.EscapeDataString(requestId)}/reject",
                new { sessionId }, Json, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Client-side subscription over the session event journal. Tracks
/// one session at a time with a monotonic cursor: duplicate deliveries of
/// the same review focus it (never stack dialogs), and a session change or
/// cancellation fences stale events. The host opens the approval dialog on
/// <see cref="ApprovalRequired"/> and closes it on
/// <see cref="SessionCancelled"/>.</summary>
public sealed class EllaiEventPoller(IEllaiReviewClient reviews)
{
    private readonly IEllaiReviewClient _reviews =
        reviews ?? throw new ArgumentNullException(nameof(reviews));

    private readonly HashSet<string> _seenReviews = new(StringComparer.Ordinal);

    public event Action<string>? ApprovalRequired;

    public event Action? SessionCancelled;

    public string? SessionId { get; private set; }

    public long Cursor { get; private set; }

    public void TrackSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!string.Equals(SessionId, sessionId, StringComparison.Ordinal))
        {
            SessionId = sessionId;
            Cursor = 0;
            _seenReviews.Clear();
        }
    }

    public void ForgetSession()
    {
        SessionId = null;
        Cursor = 0;
        _seenReviews.Clear();
    }

    public async Task PollOnceAsync(int waitSeconds, CancellationToken cancellationToken)
    {
        if (SessionId is null)
        {
            return;
        }

        var page = await _reviews
            .PollEventsAsync(SessionId, Cursor, waitSeconds, cancellationToken)
            .ConfigureAwait(false);
        if (page?.Events is null)
        {
            return;
        }

        foreach (var @event in page.Events)
        {
            if (@event.Sequence <= Cursor)
            {
                continue;
            }

            Cursor = @event.Sequence;
            if (string.Equals(@event.Kind, "approval-required", StringComparison.Ordinal)
                && @event.Ref is not null
                && _seenReviews.Add(@event.Ref))
            {
                ApprovalRequired?.Invoke(@event.Ref);
            }
            else if (string.Equals(@event.Kind, "session-cancelled", StringComparison.Ordinal))
            {
                SessionCancelled?.Invoke();
            }
        }
    }
}
