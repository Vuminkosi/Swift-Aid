using System.Net.Http.Json;
using System.Text.Json;

namespace SwiftAid.UI.Ellai;

/// <summary>Session UI event as delivered by the gateway event transport.</summary>
public sealed record EllaiSessionEventDto(
    long Sequence,
    string SessionId,
    string Kind,
    string? Ref,
    string Detail,
    DateTimeOffset AtUtc);

/// <summary>Bounded poll page with the cursor the client passes back.</summary>
public sealed record EllaiSessionEventPageDto(
    IReadOnlyList<EllaiSessionEventDto> Events,
    long Cursor);

/// <summary>Event transport port. Null means a transient hiccup: keep the
/// cursor and poll again. Cancellation stops the loop.</summary>
public interface IEllaiEventTransport
{
    Task<EllaiSessionEventPageDto?> PollAsync(
        string sessionId, long cursor, int waitSeconds, CancellationToken cancellationToken);
}

/// <summary>HTTP long-poll transport for the gateway session events endpoint.
/// Relative URLs: the host app supplies the authorized HttpClient.</summary>
public sealed class EllaiEventHttpTransport(HttpClient http) : IEllaiEventTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<EllaiSessionEventPageDto?> PollAsync(
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
}

/// <summary>Client-side subscription over the session event journal. Tracks
/// one session at a time with a monotonic cursor: duplicate deliveries of
/// the same review focus it (never stack dialogs), and a session change or
/// cancellation fences stale events. The host opens the approval dialog on
/// <see cref="ApprovalRequired"/> and closes it on
/// <see cref="SessionCancelled"/>.</summary>
public sealed class EllaiEventPoller(IEllaiEventTransport transport)
{
    private readonly IEllaiEventTransport _transport =
        transport ?? throw new ArgumentNullException(nameof(transport));

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

        var page = await _transport
            .PollAsync(SessionId, Cursor, waitSeconds, cancellationToken)
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
