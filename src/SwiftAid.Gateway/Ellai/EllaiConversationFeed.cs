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
        [Microsoft.AspNetCore.Mvc.FromServices] IEllaiSessionStore sessions,
        CancellationToken cancellationToken, bool includeProgress = false)
    {
        using var timing = EllaiPipelineMetrics.Measure("feed_read");
        var actor = ActorContext.FromValidatedPrincipal(http.User);
        var conversation = await outbox.ReadConversationAsync(sessionId, actor.ActorId, cancellationToken)
            .ConfigureAwait(false);
        var position = streamId is not null && streamId != responses.StreamId ? 0 : Math.Max(0, cursor ?? 0);
        var current = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var page = current is not null && current.Status != "Cancelled" && current.OwnerActorId == actor.ActorId
            ? responses.ReadSince(sessionId, position, includeProgress: includeProgress)
            : new EllaiSessionResponsePageDto([], position);
        foreach (var response in page.Responses)
        {
            EllaiPipelineMetrics.Record("publication_to_feed", (DateTimeOffset.UtcNow - response.CreatedAtUtc).TotalMilliseconds);
        }
        var deliverable = new List<EllaiSessionResponseDto>();
        if (current is not null && current.OwnerActorId == actor.ActorId)
        {
            foreach (var response in page.Responses)
            {
                if (!responses.TryGetProvisionalState(sessionId, response.Sequence, out var provisional)) continue;
                if (provisional &&
                    (!responses.TryGetProvisionalFence(sessionId, response.Sequence, out var attempt, out var scope, out var epoch) ||
                     outbox is not IEllaiProvisionalDeliveryFence fence ||
                     !await fence.CanDeliverProvisionalAsync(sessionId, response.ClientUtteranceId!, attempt, actor.ActorId,
                         scope, epoch, cancellationToken).ConfigureAwait(false))) continue;
                if (responses.CanDeliver(sessionId, response, current.ScopeEpoch, current.InteractionEpoch, conversation, DateTimeOffset.UtcNow))
                    deliverable.Add(response);
            }
        }
        http.Response.Headers.CacheControl = "no-store, private";
        return Results.Json(page with
        {
            StreamId = responses.StreamId,
            Responses = deliverable,
            Conversation = current is not null && current.OwnerActorId == actor.ActorId
                ? conversation.Select(EllaiConversationProjection.ToClient).ToArray() : [],
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
