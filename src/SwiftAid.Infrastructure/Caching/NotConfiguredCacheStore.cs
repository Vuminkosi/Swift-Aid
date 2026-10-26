using SwiftAid.Application.Abstractions;

namespace SwiftAid.Infrastructure.Caching;

public sealed class NotConfiguredCacheStore : ICacheStore
{
    public ValueTask<CacheReadResult> GetAsync(string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(cancellationToken.IsCancellationRequested
            ? new CacheReadResult(CacheReadStatus.Cancelled)
            : new CacheReadResult(CacheReadStatus.NotConfigured));

    public ValueTask<CacheWriteResult> SetAsync(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan timeToLive,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(cancellationToken.IsCancellationRequested
            ? new CacheWriteResult(CacheWriteStatus.Cancelled)
            : new CacheWriteResult(CacheWriteStatus.NotConfigured));

    public ValueTask<CacheWriteResult> RemoveAsync(string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(cancellationToken.IsCancellationRequested
            ? new CacheWriteResult(CacheWriteStatus.Cancelled)
            : new CacheWriteResult(CacheWriteStatus.NotConfigured));
}
