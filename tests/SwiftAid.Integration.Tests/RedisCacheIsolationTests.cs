using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Abstractions;
using SwiftAid.Infrastructure.Caching;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Integration.Tests;

public sealed class RedisCacheIsolationTests
{
    private sealed class RedisDownFactAttribute : FactAttribute
    {
        public RedisDownFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_REDIS_DOWN_CONNECTION")))
                Skip = "Requires a stopped disposable Redis fixture endpoint.";
        }
    }

    [RedisDownFact]
    public async Task UnavailableRedisReturnsExplicitFailureForReadWriteAndRemoval()
    {
        var connection = Environment.GetEnvironmentVariable("SWIFTAID_TEST_REDIS_DOWN_CONNECTION")!;
        using var cache = new RedisCache(Options.Create(new RedisCacheOptions { Configuration = connection, InstanceName = "swiftaid:test:outage:" }));
        var store = new RedisCacheStore(cache, Options.Create(new RedisOptions { MaximumTtlSeconds = 60 }),
            NullLogger<RedisCacheStore>.Instance);
        Assert.Equal(CacheReadStatus.Failed, (await store.GetAsync("key", CancellationToken.None)).Status);
        Assert.Equal(CacheWriteStatus.Failed, (await store.SetAsync("key", new byte[] { 1 }, TimeSpan.FromSeconds(10), CancellationToken.None)).Status);
        Assert.Equal(CacheWriteStatus.Failed, (await store.RemoveAsync("key", CancellationToken.None)).Status);
    }

    [DockerRedisFixtureFact]
    public async Task SeparateInstancePrefixesIsolateEntriesAndExpiryAndRemoval()
    {
        var connection = Environment.GetEnvironmentVariable("SWIFTAID_TEST_REDIS_CONNECTION")!;
        var prefix = "swiftaid:test:" + Guid.NewGuid().ToString("N") + ":";
        using var firstCache = new RedisCache(Options.Create(new RedisCacheOptions { Configuration = connection, InstanceName = prefix + "a:" }));
        using var secondCache = new RedisCache(Options.Create(new RedisCacheOptions { Configuration = connection, InstanceName = prefix + "b:" }));
        var settings = Options.Create(new RedisOptions { MaximumTtlSeconds = 60 });
        var first = new RedisCacheStore(firstCache, settings, NullLogger<RedisCacheStore>.Instance);
        var second = new RedisCacheStore(secondCache, settings, NullLogger<RedisCacheStore>.Instance);
        try
        {
            Assert.Equal(CacheWriteStatus.Written, (await first.SetAsync("same-key", new byte[] { 1 }, TimeSpan.FromSeconds(10), default)).Status);
            Assert.Equal(CacheReadStatus.Miss, (await second.GetAsync("same-key", default)).Status);
            Assert.Equal(CacheWriteStatus.Written, (await second.SetAsync("same-key", new byte[] { 2 }, TimeSpan.FromSeconds(1), default)).Status);
            Assert.Equal(new byte[] { 1 }, (await first.GetAsync("same-key", default)).Value);
            Assert.Equal(new byte[] { 2 }, (await second.GetAsync("same-key", default)).Value);
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            Assert.Equal(CacheReadStatus.Miss, (await second.GetAsync("same-key", default)).Status);
            Assert.Equal(CacheReadStatus.Hit, (await first.GetAsync("same-key", default)).Status);
            Assert.Equal(CacheWriteStatus.Removed, (await first.RemoveAsync("same-key", default)).Status);
            Assert.Equal(CacheReadStatus.Miss, (await first.GetAsync("same-key", default)).Status);
        }
        finally
        {
            await first.RemoveAsync("same-key", CancellationToken.None);
            await second.RemoveAsync("same-key", CancellationToken.None);
        }
    }
}
