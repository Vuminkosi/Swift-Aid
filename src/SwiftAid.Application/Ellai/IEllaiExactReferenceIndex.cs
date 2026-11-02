namespace SwiftAid.Application.Ellai;

/// <summary>Optional derived lookup. A hit is never record or access authority.</summary>
public sealed record EllaiExactMemoryReference(string Id, string SessionId, string ScopeEpoch,
    string TopicId, string SourceVersion, DateTimeOffset ExpiresAtUtc);

public interface IEllaiExactReferenceIndex
{
    Task<EllaiExactMemoryReference?> FindExactAsync(string id, string sessionId, string scopeEpoch, CancellationToken ct);
}
