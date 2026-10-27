using System.Security.Cryptography;
using Microsoft.Maui.Storage;
using SwiftAid.Client.Intake;

namespace SwiftAid.Mobile.Services.Intake;

public interface IGuestInstallKeyStore
{
    Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken);
}

/// <summary>Holds one app-install encryption key in the operating system's protected key store.</summary>
public sealed class GuestInstallKeyStore : IGuestInstallKeyStore
{
    private const string KeyName = "swiftaid.guest.intake.install-key.v1";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? encoded;
            try
            {
                encoded = await SecureStorage.Default.GetAsync(KeyName).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GuestSubmissionStoreUnavailableException();
            }

            if (encoded is not null)
            {
                try
                {
                    var key = Convert.FromBase64String(encoded);
                    if (key.Length == 32)
                    {
                        return key;
                    }

                    CryptographicOperations.ZeroMemory(key);
                    throw new GuestSubmissionStoreUnavailableException();
                }
                catch (FormatException)
                {
                    throw new GuestSubmissionStoreUnavailableException();
                }
            }

            var generated = RandomNumberGenerator.GetBytes(32);
            try
            {
                await SecureStorage.Default.SetAsync(KeyName, Convert.ToBase64String(generated)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var persisted = await SecureStorage.Default.GetAsync(KeyName).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (persisted is null)
                {
                    throw new GuestSubmissionStoreUnavailableException();
                }

                var verified = Convert.FromBase64String(persisted);
                if (verified.Length != 32 || !CryptographicOperations.FixedTimeEquals(generated, verified))
                {
                    CryptographicOperations.ZeroMemory(verified);
                    throw new GuestSubmissionStoreUnavailableException();
                }

                return verified;
            }
            catch (GuestSubmissionStoreUnavailableException)
            {
                throw;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GuestSubmissionStoreUnavailableException();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(generated);
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
