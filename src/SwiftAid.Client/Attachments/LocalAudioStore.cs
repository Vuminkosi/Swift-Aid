using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwiftAid.Client.LocalStore;

namespace SwiftAid.Client.Attachments;

/// <summary>Local lifecycle for an encrypted original voice note.</summary>
public enum LocalAudioState
{
    Recording,
    Interrupted,
    Ready
}

/// <summary>Safe, non-payload metadata for a local original voice note.</summary>
public sealed record LocalAudioMetadata(
    Guid AudioId,
    string WorkspaceId,
    LocalAudioState State,
    long LengthBytes,
    TimeSpan Duration,
    string? Sha256);

/// <summary>Provides an application-private root directory; never a caller-selected path.</summary>
public interface ILocalAudioPathProvider
{
    string GetAppPrivateDirectory();
}

/// <summary>Encrypted, account-bound persistence for exact original audio bytes.</summary>
public interface ILocalAudioStore
{
    Task<Guid> BeginCaptureAsync(string workspaceId, CancellationToken cancellationToken);

    Task<LocalAudioMetadata> AppendSegmentAsync(
        Guid audioId,
        ReadOnlyMemory<byte> originalBytes,
        TimeSpan segmentDuration,
        CancellationToken cancellationToken);

    Task<LocalAudioMetadata> MarkInterruptedAsync(Guid audioId, CancellationToken cancellationToken);

    /// <summary>Call during app startup to recover captures left Recording by process termination.</summary>
    Task<LocalAudioMetadata> RecoverInterruptedAsync(Guid audioId, CancellationToken cancellationToken);

    Task<LocalAudioMetadata> ResumeCaptureAsync(Guid audioId, CancellationToken cancellationToken);

    Task<LocalAudioMetadata> CompleteCaptureAsync(Guid audioId, CancellationToken cancellationToken);

    Task<LocalAudioMetadata?> GetStatusAsync(Guid audioId, CancellationToken cancellationToken);

    Task<Stream?> OpenOriginalAsync(Guid audioId, CancellationToken cancellationToken);
}

/// <summary>
/// Stores bounded audio segments as independently authenticated ciphertext. Register as a
/// device-scoped singleton so the in-process gate serializes capture and recovery operations.
/// </summary>
public sealed class LocalAudioStore : ILocalAudioStore
{
    public const int MaximumAudioBytes = 10 * 1024 * 1024;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(120);

    private const int FormatVersion = 1;
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaximumManifestBytes = 16 * 1024;
    private const int MaximumSegments = 4096;
    private static readonly byte[] ManifestMagic = Encoding.ASCII.GetBytes("SWAUDM01");
    private static readonly byte[] SegmentMagic = Encoding.ASCII.GetBytes("SWAUDS01");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILocalDraftAccessContextProvider _accessContextProvider;
    private readonly ILocalDraftKeyStore _keyStore;
    private readonly ILocalAudioPathProvider _pathProvider;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalAudioStore(
        ILocalDraftAccessContextProvider accessContextProvider,
        ILocalDraftKeyStore keyStore,
        ILocalAudioPathProvider pathProvider,
        TimeProvider? timeProvider = null)
    {
        _accessContextProvider = accessContextProvider ?? throw new ArgumentNullException(nameof(accessContextProvider));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Guid> BeginCaptureAsync(string workspaceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var access = await RequireAccessAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var audioId = Guid.NewGuid();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = GetAudioDirectory(audioId);
            var manifest = new AudioManifest(
                FormatVersion,
                audioId,
                access.ActorId,
                workspaceId,
                access.AccessEpoch,
                LocalAudioState.Recording,
                0,
                0,
                0,
                null);
            try
            {
                await WriteManifestAsync(manifest, access, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDeleteDirectory(folder);
                throw;
            }

            return audioId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAudioMetadata> AppendSegmentAsync(
        Guid audioId,
        ReadOnlyMemory<byte> originalBytes,
        TimeSpan segmentDuration,
        CancellationToken cancellationToken)
    {
        ValidateAudioId(audioId);
        if (originalBytes.IsEmpty)
        {
            throw new ArgumentException("An audio segment must contain original bytes.", nameof(originalBytes));
        }

        if (segmentDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentDuration), "An audio segment duration must be positive.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
            var manifest = await ReadManifestAsync(audioId, access, cancellationToken).ConfigureAwait(false);
            EnsureWorkspaceAccess(access, manifest.WorkspaceId);
            EnsureRecording(manifest);

            var nextLength = checked(manifest.LengthBytes + originalBytes.Length);
            var nextDuration = TimeSpan.FromTicks(checked(manifest.DurationTicks + segmentDuration.Ticks));
            if (nextLength > MaximumAudioBytes)
            {
                throw new LocalAudioLimitExceededException("The original audio exceeds the 10 MiB local limit.");
            }

            if (nextDuration > MaximumDuration)
            {
                throw new LocalAudioLimitExceededException("The original audio exceeds the 120 second local limit.");
            }

            if (manifest.SegmentCount >= MaximumSegments)
            {
                throw new LocalAudioLimitExceededException("The original audio contains too many stored segments.");
            }

            var index = manifest.SegmentCount;
            await WriteSegmentAsync(audioId, manifest, access, index, originalBytes, cancellationToken)
                .ConfigureAwait(false);

            var updated = manifest with
            {
                SegmentCount = index + 1,
                LengthBytes = nextLength,
                DurationTicks = nextDuration.Ticks,
                Sha256 = null
            };

            await RequireSameAccessAsync(access, manifest.WorkspaceId, cancellationToken).ConfigureAwait(false);
            await WriteManifestAsync(updated, access, cancellationToken).ConfigureAwait(false);
            return ToMetadata(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<LocalAudioMetadata> MarkInterruptedAsync(Guid audioId, CancellationToken cancellationToken) =>
        UpdateStateAsync(audioId, LocalAudioState.Recording, LocalAudioState.Interrupted, cancellationToken);

    public async Task<LocalAudioMetadata> RecoverInterruptedAsync(Guid audioId, CancellationToken cancellationToken)
    {
        ValidateAudioId(audioId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
            var manifest = await ReadManifestAsync(audioId, access, cancellationToken).ConfigureAwait(false);
            EnsureWorkspaceAccess(access, manifest.WorkspaceId);
            if (manifest.State == LocalAudioState.Recording)
            {
                manifest = manifest with { State = LocalAudioState.Interrupted };
                await RequireSameAccessAsync(access, manifest.WorkspaceId, cancellationToken).ConfigureAwait(false);
                await WriteManifestAsync(manifest, access, cancellationToken).ConfigureAwait(false);
            }
            else if (manifest.State is not (LocalAudioState.Interrupted or LocalAudioState.Ready))
            {
                throw new LocalAudioIntegrityException();
            }

            return ToMetadata(manifest);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAudioMetadata> ResumeCaptureAsync(Guid audioId, CancellationToken cancellationToken)
    {
        ValidateAudioId(audioId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
            var manifest = await ReadManifestAsync(audioId, access, cancellationToken).ConfigureAwait(false);
            EnsureWorkspaceAccess(access, manifest.WorkspaceId);
            if (manifest.State != LocalAudioState.Interrupted)
            {
                throw new LocalAudioStateException("Only an interrupted original recording can resume.");
            }

            manifest = manifest with { State = LocalAudioState.Recording };
            await RequireSameAccessAsync(access, manifest.WorkspaceId, cancellationToken).ConfigureAwait(false);
            await WriteManifestAsync(manifest, access, cancellationToken).ConfigureAwait(false);
            return ToMetadata(manifest);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAudioMetadata> CompleteCaptureAsync(Guid audioId, CancellationToken cancellationToken)
    {
        ValidateAudioId(audioId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
            var manifest = await ReadManifestAsync(audioId, access, cancellationToken).ConfigureAwait(false);
            EnsureWorkspaceAccess(access, manifest.WorkspaceId);
            EnsureRecording(manifest);
            if (manifest.LengthBytes <= 0 || manifest.SegmentCount <= 0)
            {
                throw new LocalAudioStateException("An original recording needs audio bytes before it can be completed.");
            }

            var hash = await ComputeHashAsync(audioId, manifest, access, cancellationToken).ConfigureAwait(false);
            var ready = manifest with { State = LocalAudioState.Ready, Sha256 = hash };
            await RequireSameAccessAsync(access, manifest.WorkspaceId, cancellationToken).ConfigureAwait(false);
            await WriteManifestAsync(ready, access, cancellationToken).ConfigureAwait(false);
            return ToMetadata(ready);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAudioMetadata?> GetStatusAsync(Guid audioId, CancellationToken cancellationToken)
    {
        ValidateAudioId(audioId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Capture authority only after acquiring the serialization gate. A queued query must
            // not use a context that was revoked while it waited for another storage operation.
            var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
            var manifestPath = GetManifestPath(audioId);
            if (!File.Exists(manifestPath)) return null;

            var manifest = await ReadManifestAsync(audioId, access, cancellationToken).ConfigureAwait(false);
            EnsureWorkspaceAccess(access, manifest.WorkspaceId);
            await RequireSameAccessAsync(access, manifest.WorkspaceId, cancellationToken).ConfigureAwait(false);
            return ToMetadata(manifest);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Stream?> OpenOriginalAsync(Guid audioId, CancellationToken cancellationToken)
    {
        ValidateAudioId(audioId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(GetManifestPath(audioId))) return null;

            var manifest = await ReadManifestAsync(audioId, access, cancellationToken).ConfigureAwait(false);
            EnsureWorkspaceAccess(access, manifest.WorkspaceId);
            if (manifest.State != LocalAudioState.Ready || string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                throw new LocalAudioStateException("The original audio is not ready for playback.");
            }

            if (manifest.LengthBytes > MaximumAudioBytes || manifest.SegmentCount is <= 0 or > MaximumSegments)
            {
                throw new LocalAudioIntegrityException();
            }

            var output = new MemoryStream(checked((int)manifest.LengthBytes));
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                for (var index = 0; index < manifest.SegmentCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var segment = await ReadSegmentAsync(audioId, manifest, access, index, cancellationToken)
                        .ConfigureAwait(false);
                    try
                    {
                        hasher.AppendData(segment);
                        await output.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(segment);
                    }
                }

                var digest = Convert.ToHexString(hasher.GetHashAndReset());
                if (output.Length != manifest.LengthBytes ||
                    !CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(digest), Encoding.ASCII.GetBytes(manifest.Sha256)))
                {
                    throw new LocalAudioIntegrityException();
                }

                await RequireSameAccessAsync(access, manifest.WorkspaceId, cancellationToken).ConfigureAwait(false);
                output.Position = 0;
                return output;
            }
            catch
            {
                await output.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LocalAudioMetadata> UpdateStateAsync(
        Guid audioId,
        LocalAudioState requiredState,
        LocalAudioState nextState,
        CancellationToken cancellationToken)
    {
        ValidateAudioId(audioId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
            var manifest = await ReadManifestAsync(audioId, access, cancellationToken).ConfigureAwait(false);
            EnsureWorkspaceAccess(access, manifest.WorkspaceId);
            if (manifest.State != requiredState)
            {
                throw new LocalAudioStateException("The original recording cannot make that state transition.");
            }

            manifest = manifest with { State = nextState };
            await RequireSameAccessAsync(access, manifest.WorkspaceId, cancellationToken).ConfigureAwait(false);
            await WriteManifestAsync(manifest, access, cancellationToken).ConfigureAwait(false);
            return ToMetadata(manifest);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> ComputeHashAsync(
        Guid audioId,
        AudioManifest manifest,
        LocalDraftAccessContext access,
        CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalBytes = 0;
        for (var index = 0; index < manifest.SegmentCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = await ReadSegmentAsync(audioId, manifest, access, index, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                totalBytes = checked(totalBytes + segment.Length);
                hasher.AppendData(segment);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(segment);
            }
        }

        if (totalBytes != manifest.LengthBytes)
        {
            throw new LocalAudioIntegrityException();
        }

        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    private async Task WriteSegmentAsync(
        Guid audioId,
        AudioManifest manifest,
        LocalDraftAccessContext access,
        int index,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken)
    {
        var key = await GetKeyAsync(access.ActorId, cancellationToken).ConfigureAwait(false);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        byte[]? encoded = null;
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext.Span, ciphertext, tag,
                CreateAssociatedData(access, manifest.WorkspaceId, audioId, "segment", index));
            encoded = EncodeBlob(SegmentMagic, nonce, tag, ciphertext);
            await WriteAtomicallyAsync(GetSegmentPath(audioId, index), encoded, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private async Task<byte[]> ReadSegmentAsync(
        Guid audioId,
        AudioManifest manifest,
        LocalDraftAccessContext access,
        int index,
        CancellationToken cancellationToken)
    {
        byte[] encoded;
        try
        {
            var info = new FileInfo(GetSegmentPath(audioId, index));
            if (!info.Exists || info.Length <= SegmentMagic.Length + NonceLength + TagLength ||
                info.Length > MaximumAudioBytes + SegmentMagic.Length + NonceLength + TagLength)
            {
                throw new LocalAudioIntegrityException();
            }

            encoded = await File.ReadAllBytesAsync(info.FullName, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            throw new LocalAudioIntegrityException();
        }

        try
        {
            byte[]? nonce = null;
            byte[]? tag = null;
            byte[]? ciphertext = null;
            byte[]? plaintext = null;
            byte[]? key = null;
            try
            {
                (nonce, tag, ciphertext) = DecodeBlob(encoded, SegmentMagic, MaximumAudioBytes);
                plaintext = new byte[ciphertext.Length];
                key = await GetKeyAsync(access.ActorId, cancellationToken).ConfigureAwait(false);
                using var aes = new AesGcm(key, TagLength);
                aes.Decrypt(nonce, ciphertext, tag, plaintext,
                    CreateAssociatedData(access, manifest.WorkspaceId, audioId, "segment", index));
                return plaintext;
            }
            catch (CryptographicException)
            {
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
                throw new LocalAudioIntegrityException();
            }
            finally
            {
                if (key is not null) CryptographicOperations.ZeroMemory(key);
                if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
                if (tag is not null) CryptographicOperations.ZeroMemory(tag);
                if (nonce is not null) CryptographicOperations.ZeroMemory(nonce);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private async Task WriteManifestAsync(
        AudioManifest manifest,
        LocalDraftAccessContext access,
        CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (plaintext.Length > MaximumManifestBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new LocalAudioIntegrityException();
        }

        byte[]? key = null;
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        byte[]? encoded = null;
        try
        {
            key = await GetKeyAsync(access.ActorId, cancellationToken).ConfigureAwait(false);
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag,
                CreateAssociatedData(access, workspaceId: null, manifest.AudioId, "manifest", segmentIndex: -1));
            encoded = EncodeBlob(ManifestMagic, nonce, tag, ciphertext);
            await WriteAtomicallyAsync(GetManifestPath(manifest.AudioId), encoded, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private async Task<AudioManifest> ReadManifestAsync(
        Guid audioId,
        LocalDraftAccessContext access,
        CancellationToken cancellationToken)
    {
        byte[] encoded;
        try
        {
            var info = new FileInfo(GetManifestPath(audioId));
            if (!info.Exists) throw new FileNotFoundException("The local audio item was not found.");
            if (info.Length <= ManifestMagic.Length + NonceLength + TagLength || info.Length > MaximumManifestBytes + ManifestMagic.Length + NonceLength + TagLength)
            {
                throw new LocalAudioIntegrityException();
            }

            encoded = await File.ReadAllBytesAsync(info.FullName, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            throw;
        }

        try
        {
            byte[]? nonce = null;
            byte[]? tag = null;
            byte[]? ciphertext = null;
            byte[]? plaintext = null;
            byte[]? key = null;
            try
            {
                (nonce, tag, ciphertext) = DecodeBlob(encoded, ManifestMagic, MaximumManifestBytes);
                plaintext = new byte[ciphertext.Length];
                key = await GetKeyAsync(access.ActorId, cancellationToken).ConfigureAwait(false);
                using var aes = new AesGcm(key, TagLength);
                aes.Decrypt(nonce, ciphertext, tag, plaintext,
                    CreateAssociatedData(access, workspaceId: null, audioId, "manifest", segmentIndex: -1));
                var manifest = JsonSerializer.Deserialize<AudioManifest>(plaintext, JsonOptions)
                    ?? throw new LocalAudioIntegrityException();
                ValidateManifest(manifest, audioId, access);
                return manifest;
            }
            catch (CryptographicException)
            {
                throw new LocalAudioAccessDeniedException();
            }
            catch (JsonException)
            {
                throw new LocalAudioIntegrityException();
            }
            finally
            {
                if (key is not null) CryptographicOperations.ZeroMemory(key);
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
                if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
                if (tag is not null) CryptographicOperations.ZeroMemory(tag);
                if (nonce is not null) CryptographicOperations.ZeroMemory(nonce);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private async Task WriteAtomicallyAsync(string path, byte[] contents, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(path) ?? throw new LocalAudioIntegrityException();
        Directory.CreateDirectory(folder);
        var temporaryPath = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private string GetAudioDirectory(Guid audioId) => Path.Combine(GetStorageRoot(), audioId.ToString("N"));

    private string GetManifestPath(Guid audioId) => Path.Combine(GetAudioDirectory(audioId), "manifest.bin");

    private string GetSegmentPath(Guid audioId, int index) =>
        Path.Combine(GetAudioDirectory(audioId), $"{index:D8}.segment");

    private string GetStorageRoot()
    {
        var root = _pathProvider.GetAppPrivateDirectory();
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            throw new LocalAudioStorageUnavailableException();
        }

        return Path.Combine(Path.GetFullPath(root), "swiftaid-audio-v1");
    }

    private async ValueTask<byte[]> GetKeyAsync(string actorId, CancellationToken cancellationToken)
    {
        byte[]? suppliedKey = null;
        try
        {
            suppliedKey = await _keyStore.GetOrCreateKeyAsync(actorId, cancellationToken).ConfigureAwait(false);
            if (suppliedKey is null || suppliedKey.Length != KeyLength)
            {
                throw new LocalAudioKeyUnavailableException();
            }

            return suppliedKey.ToArray();
        }
        catch (LocalDraftKeyUnavailableException)
        {
            throw new LocalAudioKeyUnavailableException();
        }
        catch (CryptographicException)
        {
            throw new LocalAudioKeyUnavailableException();
        }
        finally
        {
            if (suppliedKey is not null) CryptographicOperations.ZeroMemory(suppliedKey);
        }
    }

    private async ValueTask<LocalDraftAccessContext> RequireAccessAsync(
        string? workspaceId,
        CancellationToken cancellationToken)
    {
        LocalDraftAccessContext? access;
        try
        {
            access = await _accessContextProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new LocalAudioAccessDeniedException();
        }

        if (access is null || !access.IsProvisioned || !access.IsActive ||
            string.IsNullOrWhiteSpace(access.ActorId) || access.AccessEpoch < 0 ||
            access.ExpiresAtUtc.Offset != TimeSpan.Zero || access.ExpiresAtUtc <= _timeProvider.GetUtcNow() ||
            access.PermittedWorkspaceIds is null)
        {
            throw new LocalAudioAccessDeniedException();
        }

        if (workspaceId is not null) EnsureWorkspaceAccess(access, workspaceId);
        return access;
    }

    private async ValueTask RequireSameAccessAsync(
        LocalDraftAccessContext expected,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        var current = await RequireAccessAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (current.AccessEpoch != expected.AccessEpoch ||
            !string.Equals(current.ActorId, expected.ActorId, StringComparison.Ordinal))
        {
            throw new LocalAudioAccessDeniedException();
        }
    }

    private static void EnsureWorkspaceAccess(LocalDraftAccessContext access, string workspaceId)
    {
        if (!access.PermittedWorkspaceIds.Contains(workspaceId, StringComparer.Ordinal))
        {
            throw new LocalAudioAccessDeniedException();
        }
    }

    private static void EnsureRecording(AudioManifest manifest)
    {
        if (manifest.State != LocalAudioState.Recording)
        {
            throw new LocalAudioStateException("The original recording is not in a recording state.");
        }
    }

    private static void ValidateAudioId(Guid audioId)
    {
        if (audioId == Guid.Empty) throw new ArgumentException("An audio identifier is required.", nameof(audioId));
    }

    private static void ValidateManifest(
        AudioManifest manifest,
        Guid audioId,
        LocalDraftAccessContext access)
    {
        if (manifest.FormatVersion != FormatVersion || manifest.AudioId != audioId ||
            string.IsNullOrWhiteSpace(manifest.ActorId) || string.IsNullOrWhiteSpace(manifest.WorkspaceId) ||
            manifest.AccessEpoch < 0 || manifest.AccessEpoch != access.AccessEpoch ||
            !string.Equals(manifest.ActorId, access.ActorId, StringComparison.Ordinal) ||
            manifest.SegmentCount is < 0 or > MaximumSegments ||
            manifest.LengthBytes is < 0 or > MaximumAudioBytes ||
            manifest.DurationTicks < 0 || manifest.DurationTicks > MaximumDuration.Ticks ||
            !Enum.IsDefined(manifest.State) ||
            (manifest.State == LocalAudioState.Ready &&
             (manifest.LengthBytes <= 0 || manifest.SegmentCount <= 0 || !IsSha256(manifest.Sha256))) ||
            (manifest.State != LocalAudioState.Ready && manifest.Sha256 is not null))
        {
            throw new LocalAudioIntegrityException();
        }
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character)) return false;
        }

        return true;
    }

    private static LocalAudioMetadata ToMetadata(AudioManifest manifest) => new(
        manifest.AudioId,
        manifest.WorkspaceId,
        manifest.State,
        manifest.LengthBytes,
        TimeSpan.FromTicks(manifest.DurationTicks),
        manifest.Sha256);

    private static byte[] CreateAssociatedData(
        LocalDraftAccessContext access,
        string? workspaceId,
        Guid audioId,
        string purpose,
        int segmentIndex)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("swiftaid.local-audio.v1");
            writer.Write(access.ActorId);
            writer.Write(access.AccessEpoch);
            writer.Write(audioId.ToByteArray());
            writer.Write(workspaceId ?? string.Empty);
            writer.Write(purpose);
            writer.Write(segmentIndex);
        }

        return stream.ToArray();
    }

    private static byte[] EncodeBlob(byte[] magic, byte[] nonce, byte[] tag, byte[] ciphertext)
    {
        var encoded = new byte[checked(magic.Length + nonce.Length + tag.Length + ciphertext.Length)];
        magic.CopyTo(encoded, 0);
        nonce.CopyTo(encoded, magic.Length);
        tag.CopyTo(encoded, magic.Length + nonce.Length);
        ciphertext.CopyTo(encoded, magic.Length + nonce.Length + tag.Length);
        return encoded;
    }

    private static (byte[] Nonce, byte[] Tag, byte[] Ciphertext) DecodeBlob(
        byte[] encoded,
        byte[] magic,
        int maximumCiphertextLength)
    {
        var headerLength = checked(magic.Length + NonceLength + TagLength);
        if (encoded.Length <= headerLength || encoded.Length - headerLength > maximumCiphertextLength ||
            !encoded.AsSpan(0, magic.Length).SequenceEqual(magic))
        {
            throw new LocalAudioIntegrityException();
        }

        var nonce = encoded.AsSpan(magic.Length, NonceLength).ToArray();
        var tag = encoded.AsSpan(magic.Length + NonceLength, TagLength).ToArray();
        var ciphertext = encoded.AsSpan(headerLength).ToArray();
        return (nonce, tag, ciphertext);
    }

    private static void TryDeleteDirectory(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // The failed capture remains an unavailable local artifact and is never promoted.
        }
        catch (UnauthorizedAccessException)
        {
            // The failed capture remains an unavailable local artifact and is never promoted.
        }
    }

    private sealed record AudioManifest(
        int FormatVersion,
        Guid AudioId,
        string ActorId,
        string WorkspaceId,
        long AccessEpoch,
        LocalAudioState State,
        int SegmentCount,
        long LengthBytes,
        long DurationTicks,
        string? Sha256);
}

public sealed class LocalAudioAccessDeniedException : Exception
{
    public LocalAudioAccessDeniedException() : base("Current offline access does not permit this original audio.") { }
}

public sealed class LocalAudioKeyUnavailableException : Exception
{
    public LocalAudioKeyUnavailableException() : base("The protected local audio key is unavailable.") { }
}

public sealed class LocalAudioIntegrityException : Exception
{
    public LocalAudioIntegrityException() : base("The saved original audio failed integrity validation.") { }
}

public sealed class LocalAudioStateException(string message) : Exception(message) { }

public sealed class LocalAudioLimitExceededException(string message) : Exception(message) { }

public sealed class LocalAudioStorageUnavailableException : Exception
{
    public LocalAudioStorageUnavailableException() : base("The app-private audio storage location is unavailable.") { }
}
