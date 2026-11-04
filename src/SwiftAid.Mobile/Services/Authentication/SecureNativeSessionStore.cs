using System.Text.Json;
using Microsoft.Maui.Storage;
using SwiftAid.Client.Authentication;

namespace SwiftAid.Mobile.Services.Authentication;

/// <summary>Persists the native sign-in session in platform-protected secure storage.</summary>
public sealed class SecureNativeSessionStore : INativeSessionStore
{
    private const string SessionKey = "swiftaid.native.session.v1";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _timeProvider;

    public SecureNativeSessionStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task StoreAsync(NativeLoginSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.AccessToken) ||
            !string.Equals(session.AccessToken, session.AccessToken.Trim(), StringComparison.Ordinal) ||
            session.AccessToken.Any(char.IsControl) ||
            (session.RefreshToken is not null && (string.IsNullOrWhiteSpace(session.RefreshToken) ||
                session.RefreshToken.Any(char.IsControl))) ||
            (session.IdentityToken is not null && (string.IsNullOrWhiteSpace(session.IdentityToken) ||
                session.IdentityToken.Any(char.IsControl))) ||
            session.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException("The native session is invalid.");
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var serialized = JsonSerializer.Serialize(session, JsonOptions);
            await SecureStorage.Default.SetAsync(SessionKey, serialized).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<NativeLoginSession?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? serialized;
            try
            {
                serialized = await SecureStorage.Default.GetAsync(SessionKey).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                throw new NativeSessionUnavailableException();
            }

            if (string.IsNullOrEmpty(serialized))
            {
                return null;
            }

            NativeLoginSession? session;
            try
            {
                session = JsonSerializer.Deserialize<NativeLoginSession>(serialized, JsonOptions);
            }
            catch (JsonException)
            {
                RemoveStoredSession();
                return null;
            }

            if (session is null ||
                string.IsNullOrWhiteSpace(session.AccessToken) ||
                !string.Equals(session.AccessToken, session.AccessToken.Trim(), StringComparison.Ordinal) ||
                session.AccessToken.Any(char.IsControl) ||
                (session.RefreshToken is not null && (string.IsNullOrWhiteSpace(session.RefreshToken) ||
                    session.RefreshToken.Any(char.IsControl))) ||
                (session.IdentityToken is not null && (string.IsNullOrWhiteSpace(session.IdentityToken) ||
                    session.IdentityToken.Any(char.IsControl))) ||
                (session.ExpiresAtUtc <= _timeProvider.GetUtcNow() && string.IsNullOrWhiteSpace(session.RefreshToken)))
            {
                RemoveStoredSession();
                return null;
            }

            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NativeSessionUnavailableException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new NativeSessionUnavailableException();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SecureStorage.Default.Remove(SessionKey);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void RemoveStoredSession()
    {
        try
        {
            SecureStorage.Default.Remove(SessionKey);
        }
        catch (Exception)
        {
            throw new NativeSessionUnavailableException();
        }

    }
}
