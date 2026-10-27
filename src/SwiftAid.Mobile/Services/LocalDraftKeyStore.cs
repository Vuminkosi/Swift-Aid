using System.Security.Cryptography;
using System.Text;
using Microsoft.Maui.Storage;
using SwiftAid.Client.LocalStore;

namespace SwiftAid.Mobile.Services;

/// <summary>Stores a separate 256-bit draft payload key per actor in platform SecureStorage.</summary>
public sealed class LocalDraftKeyStore : ILocalDraftKeyStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async ValueTask<byte[]> GetOrCreateKeyAsync(string actorId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorId)) throw new LocalDraftKeyUnavailableException();
        cancellationToken.ThrowIfCancellationRequested();
        var alias = "swiftaid.localdraft.key." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(actorId))).ToLowerInvariant();

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var stored = await SecureStorage.Default.GetAsync(alias).ConfigureAwait(false);
                if (stored is not null)
                {
                    var existing = Convert.FromBase64String(stored);
                    if (existing.Length == 32) return existing;
                    CryptographicOperations.ZeroMemory(existing);
                    throw new LocalDraftKeyUnavailableException();
                }

                var created = RandomNumberGenerator.GetBytes(32);
                try
                {
                    await SecureStorage.Default.SetAsync(alias, Convert.ToBase64String(created)).ConfigureAwait(false);
                    var persisted = await SecureStorage.Default.GetAsync(alias).ConfigureAwait(false);
                    if (persisted is null) throw new LocalDraftKeyUnavailableException();
                    var verified = Convert.FromBase64String(persisted);
                    if (verified.Length != 32)
                    {
                        CryptographicOperations.ZeroMemory(verified);
                        throw new LocalDraftKeyUnavailableException();
                    }
                    return verified;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(created);
                }
            }
            catch (LocalDraftKeyUnavailableException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new LocalDraftKeyUnavailableException();
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
