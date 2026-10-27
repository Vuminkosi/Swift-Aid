using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Infrastructure.Identity;

public enum FacebookWebRateLimitStatus
{
    Allowed,
    Limited,
    Unavailable
}

public interface IFacebookWebLoginRateLimiter
{
    ValueTask<FacebookWebRateLimitStatus> CheckAsync(
        string clientAddress,
        int maximumAttempts,
        TimeSpan window,
        CancellationToken cancellationToken);
}

public sealed class RedisFacebookWebLoginRateLimiter : IFacebookWebLoginRateLimiter, IDisposable
{
    private const string IncrementScript = "local n=redis.call('INCR',KEYS[1]); if n==1 then redis.call('EXPIRE',KEYS[1],ARGV[1]); end; return n";
    private readonly IDatabase _database;
    private readonly byte[] _hmacKey;

    public RedisFacebookWebLoginRateLimiter(
        IConnectionMultiplexer connection,
        FederationOptions federationOptions)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(federationOptions);
        _database = connection.GetDatabase();
        _hmacKey = Convert.FromBase64String(federationOptions.FacebookWebLogin.RateLimitHmacKeyBase64);
        if (_hmacKey.Length < 32)
        {
            CryptographicOperations.ZeroMemory(_hmacKey);
            throw new InvalidOperationException("Facebook web login rate-limit key is invalid.");
        }
    }

    public async ValueTask<FacebookWebRateLimitStatus> CheckAsync(
        string clientAddress,
        int maximumAttempts,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return FacebookWebRateLimitStatus.Unavailable;
        }

        try
        {
            var key = BuildRateLimitKey(_hmacKey, clientAddress);
            var count = (long)await _database.ScriptEvaluateAsync(
                    IncrementScript,
                    [key],
                    [Math.Clamp((int)Math.Ceiling(window.TotalSeconds), 1, 3600)])
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return count <= maximumAttempts
                ? FacebookWebRateLimitStatus.Allowed
                : FacebookWebRateLimitStatus.Limited;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return FacebookWebRateLimitStatus.Unavailable;
        }
        catch (RedisException)
        {
            return FacebookWebRateLimitStatus.Unavailable;
        }
        catch (TimeoutException)
        {
            return FacebookWebRateLimitStatus.Unavailable;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return FacebookWebRateLimitStatus.Unavailable;
        }
    }

    internal static string BuildRateLimitKey(ReadOnlySpan<byte> hmacKey, string clientAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientAddress);
        var addressBytes = Encoding.UTF8.GetBytes(clientAddress);
        try
        {
            var digest = HMACSHA256.HashData(hmacKey, addressBytes);
            try
            {
                return string.Concat("swiftaid:auth:facebook-web:rate:v2:", Convert.ToHexString(digest));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(addressBytes);
        }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_hmacKey);
}

public sealed class UnavailableFacebookWebLoginRateLimiter : IFacebookWebLoginRateLimiter
{
    public ValueTask<FacebookWebRateLimitStatus> CheckAsync(
        string clientAddress,
        int maximumAttempts,
        TimeSpan window,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(FacebookWebRateLimitStatus.Unavailable);
}
