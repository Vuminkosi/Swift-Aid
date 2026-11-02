using System.Net;
using System.Text;
using System.Text.Json;

namespace SwiftAid.Infrastructure.Memory;

/// <summary>Derived, nonsemantic reference. Source version and access must be checked in PostgreSQL.</summary>
public sealed record EllaiExactMemoryReference(string Id, string SessionId, string ScopeEpoch,
    string TopicId, string SourceVersion, DateTimeOffset ExpiresAtUtc);

public sealed partial class EllaiMemoryIndex
{
    // Separate collection prevents changing the schema of a future verified vector index.
    private string ExactUrl => endpoint.TrimEnd('/') + "/collections/" + collection + "-exact";

    public Task<bool> EnsureExactCollectionAsync(CancellationToken ct) =>
        ExactWriteAsync(HttpMethod.Put, ExactUrl, new { vectors = new { } }, ct, allowConflict: true);

    public Task<bool> UpsertExactAsync(EllaiExactMemoryReference reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (new[] { reference.Id, reference.SessionId, reference.ScopeEpoch, reference.TopicId, reference.SourceVersion }
                .Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256)
            || reference.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return Task.FromResult(false);
        return ExactWriteAsync(HttpMethod.Put, ExactUrl + "/points?wait=true", new
        {
            points = new[] { new
            {
                id = ToPointId(JsonSerializer.Serialize(new[] { reference.SessionId, reference.ScopeEpoch, reference.Id })),
                vector = new { },
                payload = new
                {
                    memory_id = reference.Id, session_id = reference.SessionId, scope_epoch = reference.ScopeEpoch,
                    topic_id = reference.TopicId, source_version = reference.SourceVersion,
                    expires_at = reference.ExpiresAtUtc.ToUnixTimeSeconds(), encoding = "exact/1"
                }
            } }
        }, ct);
    }

    public async Task<EllaiExactMemoryReference?> FindExactAsync(string id, string sessionId,
        string scopeEpoch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || new[] { id, sessionId, scopeEpoch }
            .Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256)) return null;
        var now = DateTimeOffset.UtcNow;
        using var request = new HttpRequestMessage(HttpMethod.Post, ExactUrl + "/points/scroll")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                limit = 1, with_payload = true, with_vector = false,
                filter = new { must = new object[]
                {
                    new { key = "memory_id", match = new { value = id } },
                    new { key = "session_id", match = new { value = sessionId } },
                    new { key = "scope_epoch", match = new { value = scopeEpoch } },
                    new { key = "encoding", match = new { value = "exact/1" } },
                    new { key = "expires_at", range = new { gt = now.ToUnixTimeSeconds() } }
                } }
            }), Encoding.UTF8, "application/json")
        };
        Authorize(request);
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var points = document.RootElement.GetProperty("result").GetProperty("points");
            if (points.GetArrayLength() != 1) return null;
            var payload = points[0].GetProperty("payload");
            // Recheck payload even when the remote service claims it applied the filter.
            if (payload.GetProperty("memory_id").GetString() != id
                || payload.GetProperty("session_id").GetString() != sessionId
                || payload.GetProperty("scope_epoch").GetString() != scopeEpoch
                || payload.GetProperty("encoding").GetString() != "exact/1") return null;
            var expiry = DateTimeOffset.FromUnixTimeSeconds(payload.GetProperty("expires_at").GetInt64());
            var topic = payload.GetProperty("topic_id").GetString();
            var version = payload.GetProperty("source_version").GetString();
            return expiry > now && topic is { Length: > 0 and <= 256 } && version is { Length: > 0 and <= 256 }
                ? new(id, sessionId, scopeEpoch, topic, version, expiry) : null;
        }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    private async Task<bool> ExactWriteAsync(HttpMethod method, string url, object payload,
        CancellationToken ct, bool allowConflict = false)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        Authorize(request);
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode || (allowConflict && response.StatusCode == HttpStatusCode.Conflict);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return false; }
    }
}
