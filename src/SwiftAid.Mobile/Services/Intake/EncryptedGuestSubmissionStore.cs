using System.Security.Cryptography;
using System.Text.Json;
using SwiftAid.Client.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Mobile.Services.Intake;

/// <summary>Encrypted, app-private guest queue isolated from staff projections and command outboxes.</summary>
public sealed class EncryptedGuestSubmissionStore : IGuestSubmissionPersistence
{
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaximumSubmissions = 256;
    private const int MaximumEncryptedFileBytes = 16 * 1024 * 1024;
    private static readonly byte[] AssociatedData = "SwiftAid.GuestIntake.Queue.v1"u8.ToArray();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dataPath;
    private readonly string _temporaryPath;
    private readonly IGuestInstallKeyStore _keyStore;

    public EncryptedGuestSubmissionStore(string appPrivateDirectory, IGuestInstallKeyStore keyStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appPrivateDirectory);
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        var directory = Path.Combine(appPrivateDirectory, "guest-intake");
        _dataPath = Path.Combine(directory, "submissions.v1.bin");
        _temporaryPath = Path.Combine(directory, "submissions.v1.tmp");
    }

    public async Task<GuestSubmissionRecord?> GetAsync(
        Guid clientSubmissionId,
        CancellationToken cancellationToken)
    {
        if (clientSubmissionId == Guid.Empty) throw new ArgumentException("A report identifier is required.", nameof(clientSubmissionId));
        var submissions = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return submissions.SingleOrDefault(item => item.ClientSubmissionId == clientSubmissionId);
    }

    public async Task<IReadOnlyList<GuestSubmissionRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await ReadAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(GuestSubmissionRecord submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        await SaveBatchAsync([submission], cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveBatchAsync(
        IReadOnlyList<GuestSubmissionRecord> submissions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submissions);
        if (submissions.Select(item => item.ClientSubmissionId).Distinct().Count() != submissions.Count)
        {
            throw new GuestSubmissionStoreUnavailableException();
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false);
            var index = stored.ToDictionary(item => item.ClientSubmissionId);
            foreach (var item in submissions)
            {
                Validate(item);
                if (index.TryGetValue(item.ClientSubmissionId, out var existing))
                {
                    EnsureImmutableIdentity(existing, item);
                    if (!IsAllowedTransition(existing, item))
                    {
                        throw new GuestSubmissionStoreUnavailableException();
                    }

                    if (existing.State is not (GuestSubmissionLocalState.Draft or GuestSubmissionLocalState.NeedsAttention) &&
                        !(existing.AudioAssets ?? Array.Empty<GuestAudioAsset>())
                            .SequenceEqual(item.AudioAssets ?? Array.Empty<GuestAudioAsset>()))
                    {
                        throw new GuestSubmissionStoreUnavailableException();
                    }

                    if (existing.State == GuestSubmissionLocalState.Received && !IsReceiptRefresh(existing, item))
                    {
                        throw new GuestSubmissionStoreUnavailableException();
                    }
                }

                index[item.ClientSubmissionId] = item;
            }

            if (index.Count > MaximumSubmissions)
            {
                throw new GuestSubmissionStoreUnavailableException();
            }

            await WriteAllCoreAsync(index.Values.OrderBy(item => item.CreatedAtUtc).ToArray(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GuestSubmissionStoreUnavailableException)
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

    private async Task<IReadOnlyList<GuestSubmissionRecord>> ReadAllAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GuestSubmissionStoreUnavailableException)
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

    private async Task<IReadOnlyList<GuestSubmissionRecord>> ReadAllCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_dataPath))
        {
            return Array.Empty<GuestSubmissionRecord>();
        }

        if (new FileInfo(_dataPath).Length > MaximumEncryptedFileBytes)
        {
            throw new GuestSubmissionStoreUnavailableException();
        }

        var envelopeBytes = await File.ReadAllBytesAsync(_dataPath, cancellationToken).ConfigureAwait(false);
        byte[] key = [];
        byte[] plaintext = [];
        try
        {
            var envelope = JsonSerializer.Deserialize<EncryptedQueueEnvelope>(envelopeBytes, JsonOptions)
                ?? throw new GuestSubmissionStoreUnavailableException();
            if (envelope.Version != 1 || envelope.Nonce.Length != NonceLength || envelope.Tag.Length != TagLength ||
                envelope.Ciphertext.Length == 0)
            {
                throw new GuestSubmissionStoreUnavailableException();
            }

            key = await _keyStore.GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
            if (key.Length != 32) throw new GuestSubmissionStoreUnavailableException();
            plaintext = new byte[envelope.Ciphertext.Length];
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, AssociatedData);
            var submissions = JsonSerializer.Deserialize<List<GuestSubmissionRecord>>(plaintext, JsonOptions)
                ?? throw new GuestSubmissionStoreUnavailableException();
            foreach (var item in submissions) Validate(item);
            if (submissions.Select(item => item.ClientSubmissionId).Distinct().Count() != submissions.Count)
            {
                throw new GuestSubmissionStoreUnavailableException();
            }

            return submissions;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelopeBytes);
            if (key.Length > 0) CryptographicOperations.ZeroMemory(key);
            if (plaintext.Length > 0) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async Task WriteAllCoreAsync(
        IReadOnlyList<GuestSubmissionRecord> submissions,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_dataPath)!;
        Directory.CreateDirectory(directory);
        var key = await _keyStore.GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(submissions, JsonOptions);
        if (plaintext.Length > MaximumEncryptedFileBytes)
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
            throw new GuestSubmissionStoreUnavailableException();
        }
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        var envelopeBytes = Array.Empty<byte>();
        try
        {
            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
            }

            envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(
                new EncryptedQueueEnvelope(1, nonce, ciphertext, tag), JsonOptions);
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
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            if (envelopeBytes.Length > 0) CryptographicOperations.ZeroMemory(envelopeBytes);
        }
    }

    private static void Validate(GuestSubmissionRecord item)
    {
        if (item.ClientSubmissionId == Guid.Empty || item.Submission.ClientSubmissionId != item.ClientSubmissionId ||
            item.Submission.SchemaVersion != 1 || !Enum.IsDefined(item.State) ||
            item.Version <= 0 ||
            item.CreatedAtUtc.Offset != TimeSpan.Zero || item.UpdatedAtUtc.Offset != TimeSpan.Zero ||
            item.UpdatedAtUtc < item.CreatedAtUtc || string.IsNullOrWhiteSpace(item.PayloadSha256) ||
            item.PayloadSha256.Length != 64 || item.ReceiptProof.Length != 43 ||
            (item.Receipt is not null && (item.Receipt.ReceiptId == Guid.Empty ||
                item.Receipt.ClientSubmissionId != item.ClientSubmissionId ||
                !Enum.IsDefined(item.Receipt.Status) || !Enum.IsDefined(item.Receipt.RoutingStatus) ||
                item.Receipt.ReceivedAtUtc.Offset != TimeSpan.Zero)) ||
            (item.State == GuestSubmissionLocalState.Received) != (item.Receipt is not null) ||
            (item.IntakeCapability is null) != (item.CapabilityExpiresAtUtc is null) ||
            (item.CapabilityExpiresAtUtc is { Offset: var offset } && offset != TimeSpan.Zero))
        {
            throw new GuestSubmissionStoreUnavailableException();
        }

        byte[] proof;
        try
        {
            if (item.ReceiptProof.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            {
                throw new FormatException();
            }

            var normalized = item.ReceiptProof.Replace('-', '+').Replace('_', '/');
            normalized += (normalized.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw new FormatException()
            };
            proof = Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            throw new GuestSubmissionStoreUnavailableException();
        }

        try
        {
            if (proof.Length != 32) throw new GuestSubmissionStoreUnavailableException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(proof);
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(item.Submission, JsonOptions);
        var payloadHash = Convert.ToHexString(SHA256.HashData(payload));
        CryptographicOperations.ZeroMemory(payload);
        if (!string.Equals(payloadHash, item.PayloadSha256, StringComparison.Ordinal))
        {
            throw new GuestSubmissionStoreUnavailableException();
        }

        var audioAssets = item.AudioAssets ?? Array.Empty<GuestAudioAsset>();
        if (audioAssets.Select(asset => asset.AttachmentId).Distinct().Count() != audioAssets.Count ||
            audioAssets.Count > item.Submission.OriginalAudioAttachmentIds.Count ||
            audioAssets.Any(asset =>
                asset.AttachmentId == Guid.Empty ||
                !item.Submission.OriginalAudioAttachmentIds.Contains(asset.AttachmentId) ||
                asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit) ||
                string.IsNullOrWhiteSpace(asset.MediaType) || asset.MediaType.Length > 128 ||
                asset.MediaType.Any(char.IsControl) || asset.ByteLength is <= 0 or > 10 * 1024 * 1024 ||
                asset.DurationMilliseconds is <= 0 or > 120_000 ||
                asset.State != GuestAudioLocalState.LocalOnly))
        {
            throw new GuestSubmissionStoreUnavailableException();
        }
    }

    private static void EnsureImmutableIdentity(GuestSubmissionRecord existing, GuestSubmissionRecord updated)
    {
        if (!string.Equals(existing.ReceiptProof, updated.ReceiptProof, StringComparison.Ordinal) ||
            !string.Equals(existing.PayloadSha256, updated.PayloadSha256, StringComparison.Ordinal) ||
            existing.Submission.ClientSubmissionId != updated.Submission.ClientSubmissionId)
        {
            throw new GuestSubmissionStoreUnavailableException();
        }
    }

    private static bool IsReceiptRefresh(GuestSubmissionRecord existing, GuestSubmissionRecord updated) =>
        updated.State == GuestSubmissionLocalState.Received &&
        existing.Receipt is not null && updated.Receipt is not null &&
        existing.Receipt.ReceiptId == updated.Receipt.ReceiptId &&
        existing.Receipt.ClientSubmissionId == updated.Receipt.ClientSubmissionId &&
        existing.Receipt.ReceivedAtUtc == updated.Receipt.ReceivedAtUtc &&
        CanAdvanceStatus(existing.Receipt.Status, updated.Receipt.Status) &&
        CanAdvanceRouting(existing.Receipt.RoutingStatus, updated.Receipt.RoutingStatus) &&
        updated.IntakeCapability is null && updated.CapabilityExpiresAtUtc is null;

    private static bool IsAllowedTransition(GuestSubmissionRecord existing, GuestSubmissionRecord updated) =>
        existing.State switch
        {
            GuestSubmissionLocalState.Draft => updated.State is
                GuestSubmissionLocalState.Draft or GuestSubmissionLocalState.Queued or GuestSubmissionLocalState.Superseded,
            GuestSubmissionLocalState.NeedsAttention => updated.State is
                GuestSubmissionLocalState.NeedsAttention or GuestSubmissionLocalState.Superseded,
            GuestSubmissionLocalState.Queued => updated.State is
                GuestSubmissionLocalState.Queued or GuestSubmissionLocalState.Received or GuestSubmissionLocalState.NeedsAttention,
            GuestSubmissionLocalState.Received => IsReceiptRefresh(existing, updated),
            GuestSubmissionLocalState.Superseded => existing == updated,
            _ => false
        };

    private static bool CanAdvanceStatus(IntakeStatus current, IntakeStatus next) =>
        current == next || (current switch
        {
            IntakeStatus.ReceivedUnverified => true,
            IntakeStatus.AwaitingReview => next is IntakeStatus.AcceptedIntoCase or IntakeStatus.LinkedAsEvidence or IntakeStatus.Declined,
            _ => false
        });

    private static bool CanAdvanceRouting(IntakeRoutingStatus current, IntakeRoutingStatus next) =>
        current == next || (current == IntakeRoutingStatus.NeedsRouting &&
            next is IntakeRoutingStatus.RoutedForReview or IntakeRoutingStatus.NoServiceCoverage);

    private sealed record EncryptedQueueEnvelope(int Version, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
}
