namespace SwiftAid.Application.Sessions;

/// <summary>
/// Optimistic-concurrency conflict for a community-session command. The API layer
/// maps this to HTTP 409. Carries the expected (stale) and actual versions.
/// </summary>
public sealed class SessionVersionConflictException(
    Guid sessionId,
    long expectedVersion,
    long actualVersion,
    Exception? innerException = null)
    : InvalidOperationException(
        $"Community session {sessionId:D} is at version {actualVersion} but the command expected version {expectedVersion}.",
        innerException)
{
    public Guid SessionId { get; } = sessionId;
    public long ExpectedVersion { get; } = expectedVersion;
    public long ActualVersion { get; } = actualVersion;
}
