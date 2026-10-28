using System.Security.Cryptography;
using System.Text.Json;
using SwiftAid.Client.Intake;

namespace SwiftAid.Mobile.Services.Intake;

/// <summary>Persists audio-removal intents in an encrypted, app-private file.</summary>
public sealed class EncryptedGuestAudioCleanupJournal : IGuestAudioCleanupJournal
{
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaximumFileBytes = 512 * 1024;
    private const int MaximumIntents = 1024;
    private static readonly byte[] AssociatedData = "SwiftAid.GuestAudioCleanup.v1"u8.ToArray();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dataPath;
    private readonly string _temporaryPath;
    private readonly IGuestInstallKeyStore _keyStore;

    public EncryptedGuestAudioCleanupJournal(string appPrivateDirectory, IGuestInstallKeyStore keyStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appPrivateDirectory);
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        var directory = Path.Combine(appPrivateDirectory, "guest-intake", "audio-cleanup");
        _dataPath = Path.Combine(directory, "intents.v1.bin");
        _temporaryPath = Path.Combine(directory, "intents.v1.tmp");
    }

    public async Task<GuestAudioCleanupJournalResult> EnsurePendingAsync(
        GuestAudioCleanupIntent intent,
        CancellationToken cancellationToken = default)
    {
        if (intent is null || !intent.IsValid)
        {
            return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.InvalidRequest);
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadEntriesAsync(cancellationToken).ConfigureAwait(false);
            var existing = entries.SingleOrDefault(item => item.Intent.IntentKey == intent.IntentKey);
            if (existing is not null)
            {
                if (existing.Intent != intent)
                {
                    return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.InvalidRequest);
                }

                return existing.State switch
                {
                    EntryState.Pending => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.AlreadyPending),
                    EntryState.Completed => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.AlreadyCompleted),
                    _ => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.AlreadyCancelled)
                };
            }

            while (entries.Count >= MaximumIntents)
            {
                var terminalIndex = entries.FindIndex(item => item.State != EntryState.Pending);
                if (terminalIndex < 0)
                {
                    return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Unavailable);
                }

                entries.RemoveAt(terminalIndex);
            }

            entries.Add(new JournalEntry(intent, EntryState.Pending));
            await WriteEntriesAsync(entries, cancellationToken).ConfigureAwait(false);
            return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Recorded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GuestSubmissionStoreUnavailableException)
        {
            return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Unavailable);
        }
        catch (Exception)
        {
            return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Failed);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<IReadOnlyList<GuestAudioCleanupIntent>> GetPendingAsync(
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadEntriesAsync(cancellationToken).ConfigureAwait(false))
                .Where(item => item.State == EntryState.Pending)
                .Select(item => item.Intent)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new GuestSubmissionStoreUnavailableException();
        }
        finally
        {
            Gate.Release();
        }
    }

    public Task<GuestAudioCleanupJournalResult> CompleteAsync(
        string intentKey,
        CancellationToken cancellationToken = default) =>
        SetTerminalStateAsync(intentKey, EntryState.Completed, cancellationToken);

    public Task<GuestAudioCleanupJournalResult> CancelAsync(
        string intentKey,
        CancellationToken cancellationToken = default) =>
        SetTerminalStateAsync(intentKey, EntryState.Cancelled, cancellationToken);

    private async Task<GuestAudioCleanupJournalResult> SetTerminalStateAsync(
        string intentKey,
        EntryState targetState,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(intentKey) || intentKey.Length > 220)
        {
            return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.InvalidRequest);
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadEntriesAsync(cancellationToken).ConfigureAwait(false);
            var index = entries.FindIndex(item => item.Intent.IntentKey == intentKey);
            if (index < 0)
            {
                return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Failed);
            }

            var current = entries[index];
            if (current.State == targetState)
            {
                return new GuestAudioCleanupJournalResult(targetState == EntryState.Completed
                    ? GuestAudioCleanupJournalCode.AlreadyCompleted
                    : GuestAudioCleanupJournalCode.AlreadyCancelled);
            }

            if (current.State != EntryState.Pending)
            {
                return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Failed);
            }

            entries[index] = current with { State = targetState };
            await WriteEntriesAsync(entries, cancellationToken).ConfigureAwait(false);
            return new GuestAudioCleanupJournalResult(targetState == EntryState.Completed
                ? GuestAudioCleanupJournalCode.Completed
                : GuestAudioCleanupJournalCode.Cancelled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Unavailable);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<List<JournalEntry>> ReadEntriesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_dataPath))
        {
            return [];
        }

        if (new FileInfo(_dataPath).Length > MaximumFileBytes)
        {
            throw new GuestSubmissionStoreUnavailableException();
        }

        var envelopeBytes = await File.ReadAllBytesAsync(_dataPath, cancellationToken).ConfigureAwait(false);
        byte[] key = [];
        byte[] plaintext = [];
        try
        {
            var envelope = JsonSerializer.Deserialize<JournalEnvelope>(envelopeBytes, JsonOptions)
                ?? throw new GuestSubmissionStoreUnavailableException();
            if (envelope.Version != 1 || envelope.Nonce.Length != NonceLength ||
                envelope.Tag.Length != TagLength || envelope.Ciphertext.Length == 0)
            {
                throw new GuestSubmissionStoreUnavailableException();
            }

            key = await _keyStore.GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
            if (key.Length != 32)
            {
                throw new GuestSubmissionStoreUnavailableException();
            }

            plaintext = new byte[envelope.Ciphertext.Length];
            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, AssociatedData);
            }

            var entries = JsonSerializer.Deserialize<List<JournalEntry>>(plaintext, JsonOptions)
                ?? throw new GuestSubmissionStoreUnavailableException();
            if (entries.Count > MaximumIntents || entries.Any(item => item is null ||
                    item.Intent is null || !item.Intent.IsValid || !Enum.IsDefined(item.State)) ||
                entries.Select(item => item.Intent.IntentKey).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            {
                throw new GuestSubmissionStoreUnavailableException();
            }

            return entries;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelopeBytes);
            if (key.Length > 0) CryptographicOperations.ZeroMemory(key);
            if (plaintext.Length > 0) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async Task WriteEntriesAsync(List<JournalEntry> entries, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_dataPath)!;
        Directory.CreateDirectory(directory);
        var key = await _keyStore.GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        byte[] envelopeBytes = [];
        try
        {
            if (key.Length != 32 || plaintext.Length > MaximumFileBytes)
            {
                throw new GuestSubmissionStoreUnavailableException();
            }

            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
            }

            envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(new JournalEnvelope(1, nonce, ciphertext, tag), JsonOptions);
            await using (var stream = new FileStream(
                _temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(envelopeBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(_temporaryPath, _dataPath, overwrite: true);
        }
        finally
        {
            if (key.Length > 0) CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            if (envelopeBytes.Length > 0) CryptographicOperations.ZeroMemory(envelopeBytes);
        }
    }

    private sealed record JournalEntry(GuestAudioCleanupIntent Intent, EntryState State);

    private sealed record JournalEnvelope(int Version, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

    private enum EntryState
    {
        Pending,
        Completed,
        Cancelled
    }
}
