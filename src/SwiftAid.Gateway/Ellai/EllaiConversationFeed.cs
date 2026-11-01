using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>The chunk feed is transient; its durable snapshot repairs missed publications.</summary>
public static class EllaiConversationFeed
{
    public static async Task<IResult> ListSessionsAsync(
        HttpContext http, [Microsoft.AspNetCore.Mvc.FromServices] IEllaiSessionStore sessions,
        CancellationToken cancellationToken, int offset = 0)
    {
        if (offset < 0 || offset > int.MaxValue - 20) return Results.BadRequest();
        var actor = ActorContext.FromValidatedPrincipal(http.User);
        var page = await ListOwnedSessionsAsync(sessions, actor.ActorId, offset, cancellationToken).ConfigureAwait(false);
        http.Response.Headers.CacheControl = "no-store, private";
        return Results.Json(page);
    }

    public static async Task<EllaiSessionHistoryPageDto> ListOwnedSessionsAsync(
        IEllaiSessionStore sessions, string actorId, int offset, CancellationToken cancellationToken)
    {
        var rows = await sessions.ListOwnedAsync(actorId, offset, 21, cancellationToken).ConfigureAwait(false);
        return new(rows.Take(20).Select(session => new EllaiSessionHistoryItemDto(
            session.SessionId, session.Mode.ToString(), session.Status, session.UpdatedAtUtc,
            session.HistoryPreview, session.HistoryTurnCount)).ToArray(),
            rows.Count > 20 ? offset + 20 : null);
    }

    public static async Task<IResult> ReadAsync(
        HttpContext http, string sessionId, long? cursor, string? streamId,
        [Microsoft.AspNetCore.Mvc.FromServices] IEllaiUtteranceOutboxStore outbox,
        [Microsoft.AspNetCore.Mvc.FromServices] EllaiSessionResponseStore responses,
        CancellationToken cancellationToken, bool includeProgress = false)
    {
        using var timing = EllaiPipelineMetrics.Measure("feed_read");
        var actor = ActorContext.FromValidatedPrincipal(http.User);
        var conversation = await outbox.ReadConversationAsync(sessionId, actor.ActorId, cancellationToken)
            .ConfigureAwait(false);
        var position = streamId is not null && streamId != responses.StreamId ? 0 : Math.Max(0, cursor ?? 0);
        var page = responses.ReadSince(sessionId, position, includeProgress: includeProgress);
        foreach (var response in page.Responses)
        {
            EllaiPipelineMetrics.Record("publication_to_feed", (DateTimeOffset.UtcNow - response.CreatedAtUtc).TotalMilliseconds);
        }
        http.Response.Headers.CacheControl = "no-store, private";
        return Results.Json(page with
        {
            StreamId = responses.StreamId,
            Conversation = conversation.Select(turn => new EllaiConversationTurnDto(
                turn.ClientUtteranceId, turn.Sequence, turn.UserText, turn.AssistantText,
                turn.Outcome, turn.AttemptCount, turn.CompletedAtUtc)).ToArray(),
        });
    }

    public static async Task<IResult> ReadSessionAsync(
        HttpContext http, string sessionId, EllaiSessionService sessions, CancellationToken cancellationToken)
    {
        var view = await sessions.GetOwnedViewAsync(sessionId, http.User, cancellationToken).ConfigureAwait(false);
        http.Response.Headers.CacheControl = "no-store, private";
        return view is null ? Results.NotFound() : Results.Json(view);
    }
}
