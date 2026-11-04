using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SwiftAid.Infrastructure.Memory;

/// <summary>Scoped memory reference stored as a Qdrant payload. Qdrant is a
/// private derived index: it holds references and deniable summaries, never
/// authorization, case narratives, secrets or original audio.</summary>
public sealed record EllaiMemoryPoint(
    string Id,
    string SessionId,
    string ScopeEpoch,
    string TopicId,
    string Summary,
    IReadOnlyList<string> EvidenceIds,
    DateTimeOffset CreatedAtUtc);

/// <summary>Qdrant-backed derived recall for Ellai (ELL-T09). Every read and
/// write is scoped to the current session/scope epoch; a recalled reference
/// is never authorization and must be rechecked against PostgreSQL before
/// use. Vectors are caller-supplied (no encoder here); without an encoder
/// the index still supports exact-ID lookup. Keys travel in headers only.</summary>
public sealed partial class EllaiMemoryIndex(HttpClient http, string endpoint, string apiKey, string collection)
{
    public const int DefaultVectorSize = 128;

    public async Task<bool> EnsureCollectionAsync(int vectorSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || vectorSize <= 0)
        {
            return false;
        }

        var payload = JsonSerializer.Serialize(new
        {
            vectors = new { size = vectorSize, distance = "Cosine" }
        });
        using var request = new HttpRequestMessage(HttpMethod.Put,
            endpoint.TrimEnd('/') + "/collections/" + collection)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        Authorize(request);
        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode
                || response.StatusCode == System.Net.HttpStatusCode.Conflict;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<bool> UpsertAsync(
        EllaiMemoryPoint point, IReadOnlyList<float> vector, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(vector);
        if (string.IsNullOrWhiteSpace(endpoint) || vector.Count == 0)
        {
            return false;
        }

        var payload = JsonSerializer.Serialize(new
        {
            points = new[]
            {
                new
                {
                    id = ToPointId(point.Id),
                    vector = vector,
                    payload = new
                    {
                        memory_id = point.Id,
                        session_id = point.SessionId,
                        scope_epoch = point.ScopeEpoch,
                        topic_id = point.TopicId,
                        summary = point.Summary,
                        evidence_ids = point.EvidenceIds,
                        created_at_utc = point.CreatedAtUtc,
                    }
                }
            }
        });
        using var request = new HttpRequestMessage(HttpMethod.Put,
            endpoint.TrimEnd('/') + "/collections/" + collection + "/points?wait=true")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        Authorize(request);
        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Scoped similarity recall. The server-side filter pins the
    /// session and scope epoch so one session can never read another's
    /// memory through this path.</summary>
    public async Task<IReadOnlyList<EllaiMemoryPoint>> SearchAsync(
        IReadOnlyList<float> vector, string sessionId, string scopeEpoch, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(scopeEpoch) || vector.Count == 0 || limit is < 1 or > 20)
        {
            return Array.Empty<EllaiMemoryPoint>();
        }

        var payload = JsonSerializer.Serialize(new
        {
            vector,
            limit,
            with_payload = true,
            filter = new
            {
                must = new object[]
                {
                    new { key = "session_id", match = new { value = sessionId } },
                    new { key = "scope_epoch", match = new { value = scopeEpoch } },
                }
            }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post,
            endpoint.TrimEnd('/') + "/collections/" + collection + "/points/search")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        Authorize(request);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<EllaiMemoryPoint>();
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return ParsePoints(body)
                .Where(point => string.Equals(point.SessionId, sessionId, StringComparison.Ordinal) &&
                    string.Equals(point.ScopeEpoch, scopeEpoch, StringComparison.Ordinal))
                .Take(limit)
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Array.Empty<EllaiMemoryPoint>();
        }
    }

    /// <summary>Qdrant point ids must be uint or UUID: derive a stable UUID
    /// from the application memory id so upserts stay idempotent.</summary>
    internal static Guid ToPointId(string memoryId)
    {
        var hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("ellai-memory:" + memoryId));
        return new Guid(hash);
    }

    private void Authorize(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("api-key", apiKey);
        }
    }

    private static IReadOnlyList<EllaiMemoryPoint> ParsePoints(string body)
    {
        var results = new List<EllaiMemoryPoint>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("result", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (!item.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string Get(string name) =>
                    p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? string.Empty : string.Empty;
                var id = Get("memory_id");
                if (string.IsNullOrEmpty(id) && item.TryGetProperty("id", out var i))
                {
                    id = i.ValueKind == JsonValueKind.String ? i.GetString() ?? string.Empty : i.ToString();
                }
                var evidence = new List<string>();
                if (p.TryGetProperty("evidence_ids", out var e) && e.ValueKind == JsonValueKind.Array)
                {
                    evidence.AddRange(e.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!));
                }

                results.Add(new EllaiMemoryPoint(id, Get("session_id"), Get("scope_epoch"),
                    Get("topic_id"), Get("summary"), evidence, DateTimeOffset.UtcNow));
            }
        }
        catch (JsonException)
        {
        }

        return results;
    }
}
