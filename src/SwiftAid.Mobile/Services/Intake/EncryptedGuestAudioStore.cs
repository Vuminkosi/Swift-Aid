using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SwiftAid.Client.Intake;

namespace SwiftAid.Mobile.Services.Intake;

/// <summary>Stores original guest recordings encrypted, outside staff media and Datasync stores.</summary>
public sealed class EncryptedGuestAudioStore : IGuestAudioStore
{
    private const int MaximumAudioBytes = 10 * 1024 * 1024;
    private const int MaximumDurationMilliseconds = 120_000;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int HeaderLength = 1 + NonceLength + TagLength + sizeof(int);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _audioDirectory;
    private readonly IGuestInstallKeyStore _keyStore;
    private readonly IGuestSubmissionPersistence _submissionStore;
    private readonly TimeProvider _timeProvider;

    public EncryptedGuestAudioStore(
        string appPrivateDirectory,
        IGuestInstallKeyStore keyStore,
        IGuestSubmissionPersistence submissionStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appPrivateDirectory);
        _audioDirectory = Path.Combine(appPrivateDirectory, "guest-intake", "audio");
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _submissionStore = submissionStore ?? throw new ArgumentNullException(nameof(submissionStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<GuestAudioAsset> SaveOriginalAsync(
        Guid clientSubmissionId,
        Guid attachmentId,
        string mediaType,
        int durationMilliseconds,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        if (clientSubmissionId == Guid.Empty || attachmentId == Guid.Empty)
            throw new ArgumentException("A report and audio identifier are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentNullException.ThrowIfNull(content);
        if (!mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Any(char.IsControl) || mediaType.Length > 128 ||
            durationMilliseconds is <= 0 or > MaximumDurationMilliseconds)
        {
            throw new ArgumentException("The recording is not within the supported limits.");
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[] original = [];
        try
        {
            var submission = await _submissionStore.GetAsync(clientSubmissionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The saved report could not be found.");
            if (submission.State is not (GuestSubmissionLocalState.Draft or GuestSubmissionLocalState.NeedsAttention) ||
                !submission.Submission.OriginalAudioAttachmentIds.Contains(attachmentId))
            {
                throw new InvalidOperationException("This recording is not part of an editable report.");
            }

            original = await ReadBoundedAsync(content, cancellationToken).ConfigureAwait(false);
            var hash = Convert.ToHexString(SHA256.HashData(original));
            var asset = new GuestAudioAsset(
                attachmentId,
                hash,
                mediaType,
                original.LongLength,
                durationMilliseconds,
                GuestAudioLocalState.LocalOnly);
            var existingAssets = submission.AudioAssets ?? Array.Empty<GuestAudioAsset>();
            var existing = existingAssets.SingleOrDefault(item => item.AttachmentId == attachmentId);
            if (existing is not null)
            {
                if (existing == asset)
                {
                    return existing;
                }

                throw new InvalidOperationException("Original audio cannot be replaced after it is saved.");
            }

            await WriteEncryptedAsync(asset, original, cancellationToken).ConfigureAwait(false);
            await _submissionStore.SaveAsync(submission with
            {
                AudioAssets = existingAssets.Append(asset).ToArray(),
                UpdatedAtUtc = _timeProvider.GetUtcNow()
            }, cancellationToken).ConfigureAwait(false);
            return asset;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GuestSubmissionStoreUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or CryptographicException or UnauthorizedAccessException)
        {
            throw new GuestSubmissionStoreUnavailableException();
        }
        finally
        {
            if (original.Length > 0) CryptographicOperations.ZeroMemory(original);
            Gate.Release();
        }
    }

    public async Task<byte[]> ReadOriginalAsync(
        Guid clientSubmissionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (clientSubmissionId == Guid.Empty || attachmentId == Guid.Empty)
            throw new ArgumentException("A report and audio identifier are required.");
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[] fileBytes = [];
        byte[] key = [];
        byte[] plaintext = [];
        try
        {
            var submission = await _submissionStore.GetAsync(clientSubmissionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The saved report could not be found.");
            var asset = (submission.AudioAssets ?? Array.Empty<GuestAudioAsset>())
                .SingleOrDefault(item => item.AttachmentId == attachmentId);
            if (asset is null || !submission.Submission.OriginalAudioAttachmentIds.Contains(attachmentId))
            {
                throw new FileNotFoundException("The original recording is not available.");
            }

            var path = GetPath(attachmentId);
            fileBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (fileBytes.Length < HeaderLength || fileBytes.Length > HeaderLength + MaximumAudioBytes || fileBytes[0] != 1)
            {
                throw new CryptographicException();
            }

            var contentLength = BinaryPrimitives.ReadInt32BigEndian(fileBytes.AsSpan(1 + NonceLength + TagLength, sizeof(int)));
            if (contentLength <= 0 || contentLength > MaximumAudioBytes || fileBytes.Length != HeaderLength + contentLength ||
                contentLength != asset.ByteLength)
            {
                throw new CryptographicException();
            }

            key = await _keyStore.GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
            if (key.Length != 32) throw new CryptographicException();
            plaintext = new byte[contentLength];
            var nonce = fileBytes.AsSpan(1, NonceLength);
            var tag = fileBytes.AsSpan(1 + NonceLength, TagLength);
            var ciphertext = fileBytes.AsSpan(HeaderLength, contentLength);
            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, CreateAssociatedData(attachmentId));
            }

            var hash = Convert.ToHexString(SHA256.HashData(plaintext));
            if (!string.Equals(hash, asset.Sha256, StringComparison.Ordinal))
            {
                throw new CryptographicException();
            }

            var result = plaintext;
            plaintext = [];
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GuestSubmissionStoreUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or CryptographicException or UnauthorizedAccessException)
        {
            throw new GuestSubmissionStoreUnavailableException();
        }
        finally
        {
            if (fileBytes.Length > 0) CryptographicOperations.ZeroMemory(fileBytes);
            if (key.Length > 0) CryptographicOperations.ZeroMemory(key);
            if (plaintext.Length > 0) CryptographicOperations.ZeroMemory(plaintext);
            Gate.Release();
        }
    }

    private async Task WriteEncryptedAsync(
        GuestAudioAsset asset,
        byte[] original,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_audioDirectory);
        var key = await _keyStore.GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[original.Length];
        var tag = new byte[TagLength];
        var envelope = new byte[HeaderLength + ciphertext.Length];
        try
        {
            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Encrypt(nonce, original, ciphertext, tag, CreateAssociatedData(asset.AttachmentId));
            }

            envelope[0] = 1;
            nonce.CopyTo(envelope, 1);
            tag.CopyTo(envelope, 1 + NonceLength);
            BinaryPrimitives.WriteInt32BigEndian(envelope.AsSpan(1 + NonceLength + TagLength), ciphertext.Length);
            ciphertext.CopyTo(envelope, HeaderLength);

            var path = GetPath(asset.AttachmentId);
            var temporaryPath = path + ".tmp";
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private string GetPath(Guid attachmentId) => Path.Combine(_audioDirectory, $"{attachmentId:N}.v1.bin");

    private static byte[] CreateAssociatedData(Guid attachmentId) =>
        Encoding.UTF8.GetBytes($"SwiftAid.GuestAudio.v1:{attachmentId:D}");

    private static async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaximumAudioBytes)
                {
                    throw new ArgumentException("The recording is larger than the supported limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (output.Length == 0) throw new ArgumentException("The recording is empty.");
            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var segment) && segment.Array is not null)
            {
                CryptographicOperations.ZeroMemory(segment.Array.AsSpan());
            }
        }
    }
}
