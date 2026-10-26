using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Abstractions;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Infrastructure.Caching;

public sealed class RedisCacheStore : ICacheStore
{
    private readonly IDistributedCache _cache;
    private readonly RedisOptions _options;
    private readonly ILogger<RedisCacheStore> _logger;

    public RedisCacheStore(
        IDistributedCache cache,
        IOptions<RedisOptions> options,
        ILogger<RedisCacheStore> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<CacheReadResult> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || cancellationToken.IsCancellationRequested)
        {
            return cancellationToken.IsCancellationRequested
                ? new CacheReadResult(CacheReadStatus.Cancelled)
                : new CacheReadResult(CacheReadStatus.Failed, FailureCode: "invalid-key");
        }

        try
        {
            var value = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
            return value is null
                ? new CacheReadResult(CacheReadStatus.Miss)
                : new CacheReadResult(CacheReadStatus.Hit, value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CacheReadResult(CacheReadStatus.Cancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning("Redis cache read failed. Failure type: {FailureType}", exception.GetType().Name);
            return new CacheReadResult(CacheReadStatus.Failed, FailureCode: "cache-read-failure");
        }
    }

    public async ValueTask<CacheWriteResult> SetAsync(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan timeToLive,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new CacheWriteResult(CacheWriteStatus.Cancelled);
        }

        if (string.IsNullOrWhiteSpace(key) ||
            timeToLive <= TimeSpan.Zero ||
            timeToLive > TimeSpan.FromSeconds(_options.MaximumTtlSeconds))
        {
            return new CacheWriteResult(CacheWriteStatus.Failed, "invalid-key-or-ttl");
        }

        try
        {
            await _cache.SetAsync(
                key,
                value.ToArray(),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = timeToLive
                },
                cancellationToken).ConfigureAwait(false);
            return new CacheWriteResult(CacheWriteStatus.Written);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CacheWriteResult(CacheWriteStatus.Cancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning("Redis cache write failed. Failure type: {FailureType}", exception.GetType().Name);
            return new CacheWriteResult(CacheWriteStatus.Failed, "cache-write-failure");
        }
    }

    public async ValueTask<CacheWriteResult> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new CacheWriteResult(CacheWriteStatus.Cancelled);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return new CacheWriteResult(CacheWriteStatus.Failed, "invalid-key");
        }

        try
        {
            await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return new CacheWriteResult(CacheWriteStatus.Removed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CacheWriteResult(CacheWriteStatus.Cancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning("Redis cache removal failed. Failure type: {FailureType}", exception.GetType().Name);
            return new CacheWriteResult(CacheWriteStatus.Failed, "cache-remove-failure");
        }
    }
}
