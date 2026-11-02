using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Ellai;
using SwiftAid.Infrastructure.Memory;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Rebuildable derived-index reconciliation from durable PostgreSQL topics.
/// No work is acknowledged or removed from the source; restart/outage retries are idempotent.
/// This does not replace or participate in the accepted-turn outbox.</summary>
public sealed class EllaiMemoryProjectionWorker(IServiceScopeFactory scopes,
    ILogger<EllaiMemoryProjectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var index = scope.ServiceProvider.GetRequiredService<EllaiMemoryIndex>();
                var db = scope.ServiceProvider.GetRequiredService<SwiftAidDbContext>();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                if (!await index.EnsureExactCollectionAsync(timeout.Token).ConfigureAwait(false))
                    throw new InvalidOperationException("Derived collection is unavailable.");
                var since = DateTimeOffset.UtcNow.AddHours(-24);
                var rows = await (from topic in db.Set<EllaiTopicEntity>().AsNoTracking()
                                  join session in db.Set<EllaiSessionEntity>().AsNoTracking() on topic.SessionId equals session.SessionId
                                  where topic.Title == EllaiCaseResultMemory.TopicTitle && topic.EntityVersion == "case-results/1"
                                      && topic.UpdatedAtUtc > since && session.Status == "Active"
                                  orderby topic.TopicId
                                  select new { Topic = topic, session.OwnerActorId, session.ScopeEpoch })
                    .Skip(offset).Take(50).ToListAsync(timeout.Token).ConfigureAwait(false);
                var written = 0;
                foreach (var row in rows)
                {
                    var reference = CreateReference(row.Topic, row.OwnerActorId, row.ScopeEpoch);
                    if (reference is null) continue;
                    if (!await index.UpsertExactAsync(reference, timeout.Token).ConfigureAwait(false))
                        throw new InvalidOperationException("Derived write is unavailable.");
                    written++;
                }
                if (written > 0) logger.LogInformation("Ellai exact memory projection indexed {Count} source references.", written);
                offset = rows.Count == 50 ? offset + 50 : 0;
                await Task.Delay(offset == 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Revisit the durable source page; no source work is discarded on failure.
                logger.LogWarning("Ellai exact memory projection will retry: {FailureType}.", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    public static EllaiExactMemoryReference? CreateReference(EllaiTopicEntity topic, string? ownerActorId, string scopeEpoch)
    {
        if (string.IsNullOrEmpty(ownerActorId) || topic.EntityRef is not { Length: > 0 and <= 16000 } source) return null;
        try
        {
            using var json = JsonDocument.Parse(source);
            var root = json.RootElement;
            if (root.GetProperty("ActorId").GetString() != ownerActorId || root.GetProperty("ScopeEpoch").GetString() != scopeEpoch)
                return null;
            var expires = root.GetProperty("ExpiresAtUtc").GetDateTimeOffset();
            if (expires <= DateTimeOffset.UtcNow) return null;
            return new(EllaiCaseResultMemory.Reference, topic.SessionId, scopeEpoch, topic.TopicId,
                EllaiCaseResultMemory.SourceVersion(source), expires);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }
}
