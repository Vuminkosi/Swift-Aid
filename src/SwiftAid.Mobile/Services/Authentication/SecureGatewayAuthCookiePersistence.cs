using System.Text.Json;
using Microsoft.Maui.Storage;
using SwiftAid.Client.Authentication;

namespace SwiftAid.Mobile.Services.Authentication;

/// <summary>Persists only the Gateway auth cookie through the platform keychain/keystore.</summary>
public sealed class SecureGatewayAuthCookiePersistence : IGatewayAuthCookiePersistence
{
    private const string StorageKey = "swiftaid.gateway.auth-cookie.v1";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GatewayAuthCookieSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? serialized;
            try
            {
                serialized = await SecureStorage.Default.GetAsync(StorageKey).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("The saved sign-in session is unavailable on this device.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(serialized))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<GatewayAuthCookieSnapshot>(serialized, JsonOptions);
            }
            catch (JsonException)
            {
                SecureStorage.Default.Remove(StorageKey);
                return null;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task WriteAsync(
        GatewayAuthCookieSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Uri.TryCreate(snapshot.GatewayOrigin, UriKind.Absolute, out var gateway) ||
            snapshot.RestoreFor(gateway, DateTimeOffset.UtcNow) is null)
        {
            throw new ArgumentException("The Gateway authentication cookie is invalid.", nameof(snapshot));
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var serialized = JsonSerializer.Serialize(snapshot, JsonOptions);
            await SecureStorage.Default.SetAsync(StorageKey, serialized).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The sign-in session could not be saved securely on this device.");
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SecureStorage.Default.Remove(StorageKey);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The saved sign-in session could not be cleared securely.");
        }
        finally
        {
            Gate.Release();
        }
    }
}
