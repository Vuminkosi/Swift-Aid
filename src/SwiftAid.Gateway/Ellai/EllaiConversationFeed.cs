using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>The chunk feed is transient; its durable snapshot repairs missed publications.</summary>
public static class EllaiConversationFeed
{
    public static async Task<IResult> ReadAsync(
        HttpContext http, string sessionId, long? cursor, string? streamId,
        [Microsoft.AspNetCore.Mvc.FromServices] IEllaiUtteranceOutboxStore outbox,
        [Microsoft.AspNetCore.Mvc.FromServices] EllaiSessionResponseStore responses,
        CancellationToken cancellationToken)
    {
        using var timing = EllaiPipelineMetrics.Measure("feed_read");
        var actor = ActorContext.FromValidatedPrincipal(http.User);
        var conversation = await outbox.ReadConversationAsync(sessionId, actor.ActorId, cancellationToken)
            .ConfigureAwait(false);
        var position = streamId is not null && streamId != responses.StreamId ? 0 : Math.Max(0, cursor ?? 0);
        var page = responses.ReadSince(sessionId, position);
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
