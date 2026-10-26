namespace SwiftAid.Application.Abstractions;

public enum CacheReadStatus
{
    Hit,
    Miss,
    NotConfigured,
    Failed,
    Cancelled
}

public sealed record CacheReadResult(CacheReadStatus Status, byte[]? Value = null, string? FailureCode = null);

public enum CacheWriteStatus
{
    Written,
    Removed,
    NotConfigured,
    Failed,
    Cancelled
}

public sealed record CacheWriteResult(CacheWriteStatus Status, string? FailureCode = null);

/// <summary>
/// Derived-data cache only. It must not store or establish access grants, permissions, approvals,
/// authoritative records, or idempotency receipts. Every write requires an explicit expiration.
/// </summary>
public interface ICacheStore
{
    ValueTask<CacheReadResult> GetAsync(string key, CancellationToken cancellationToken);

    ValueTask<CacheWriteResult> SetAsync(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan timeToLive,
        CancellationToken cancellationToken);

    ValueTask<CacheWriteResult> RemoveAsync(string key, CancellationToken cancellationToken);
}
