using SwiftAid.Application.Abstractions;
using SwiftAid.Application.Attachments;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Identity;

public enum ProfileAvatarStatus
{
    Saved,
    Deleted,
    Invalid,
    TooLarge,
    UnsupportedType,
    NotConfigured,
    NotFound,
    Unavailable
}

public sealed record ProfileAvatarResult(ProfileAvatarStatus Status, string? ContentType = null);

public sealed record ProfileAvatarData(byte[] Content, string ContentType);

public interface IProfileAvatarService
{
    Task<ProfileAvatarResult> SaveAsync(
        string userId,
        string contentType,
        Stream content,
        CancellationToken cancellationToken);

    Task<(ProfileAvatarResult Result, ProfileAvatarData? Data)> ReadAsync(
        string userId,
        CancellationToken cancellationToken);

    Task<ProfileAvatarResult> DeleteAsync(string userId, CancellationToken cancellationToken);

    Task<bool> HasAvatarAsync(string userId, CancellationToken cancellationToken);
}

/// <summary>Owner-scoped profile photos in private object storage. Replacing a photo
/// orphans the previous object; storage has no delete primitive. References are
/// opaque and never carry authorization.</summary>
public sealed class ProfileAvatarService(
    SwiftAidDbContext dbContext,
    IObjectStorage objectStorage,
    TimeProvider timeProvider) : IProfileAvatarService
{
    public const long MaxAvatarBytes = 2 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, byte[]> Signatures = new Dictionary<string, byte[]>(
        StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = [0xFF, 0xD8, 0xFF],
        ["image/png"] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
        ["image/webp"] = [0x52, 0x49, 0x46, 0x46] // RIFF....WEBP checked below
    };

    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IObjectStorage _objectStorage = objectStorage ?? throw new ArgumentNullException(nameof(objectStorage));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<ProfileAvatarResult> SaveAsync(
        string userId,
        string contentType,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return new ProfileAvatarResult(ProfileAvatarStatus.Invalid);
        }

        var normalizedType = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        if (!Signatures.ContainsKey(normalizedType))
        {
            return new ProfileAvatarResult(ProfileAvatarStatus.UnsupportedType);
        }

        byte[] bytes;
        try
        {
            bytes = await ReadCappedAsync(content, MaxAvatarBytes + 1, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ProfileAvatarResult(ProfileAvatarStatus.Unavailable);
        }

        if (bytes.Length == 0 || bytes.Length > MaxAvatarBytes)
        {
            return new ProfileAvatarResult(ProfileAvatarStatus.TooLarge);
        }

        if (!MatchesSignature(normalizedType, bytes))
        {
            return new ProfileAvatarResult(ProfileAvatarStatus.UnsupportedType);
        }

        ObjectStorageResult stored;
        try
        {
            using var upload = new MemoryStream(bytes, writable: false);
            stored = await _objectStorage.StoreAsync(upload, normalizedType, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ProfileAvatarResult(ProfileAvatarStatus.Unavailable);
        }

        if (stored.Status == ObjectStorageStatus.NotConfigured)
        {
            return new ProfileAvatarResult(ProfileAvatarStatus.NotConfigured);
        }

        if (stored.Status == ObjectStorageStatus.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (stored.Status != ObjectStorageStatus.Succeeded || stored.Reference is null)
        {
            return new ProfileAvatarResult(ProfileAvatarStatus.Unavailable);
        }

        try
        {
            var now = _timeProvider.GetUtcNow();
            var entity = await _dbContext.UserProfiles
                .FindAsync([userId], cancellationToken)
                .ConfigureAwait(false);
            if (entity is null)
            {
                _dbContext.UserProfiles.Add(new UserProfileEntity
                {
                    UserId = userId,
                    AvatarObjectId = stored.Reference.Id,
                    AvatarContentType = normalizedType,
                    UpdatedAtUtc = now
                });
            }
            else
            {
                entity.AvatarObjectId = stored.Reference.Id;
                entity.AvatarContentType = normalizedType;
                entity.UpdatedAtUtc = now;
            }

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ProfileAvatarResult(ProfileAvatarStatus.Saved, normalizedType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ProfileAvatarResult(ProfileAvatarStatus.Unavailable);
        }
    }

    public async Task<(ProfileAvatarResult Result, ProfileAvatarData? Data)> ReadAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return (new ProfileAvatarResult(ProfileAvatarStatus.NotFound), null);
        }

        try
        {
            var entity = await _dbContext.UserProfiles
                .FindAsync([userId], cancellationToken)
                .ConfigureAwait(false);
            if (entity?.AvatarObjectId is not { } objectId ||
                string.IsNullOrWhiteSpace(entity.AvatarContentType))
            {
                return (new ProfileAvatarResult(ProfileAvatarStatus.NotFound), null);
            }

            using var buffer = new MemoryStream();
            var copy = await _objectStorage.CopyToAsync(
                new ObjectReference(objectId), buffer, cancellationToken).ConfigureAwait(false);
            if (copy.Status == ObjectStorageStatus.NotFound)
            {
                return (new ProfileAvatarResult(ProfileAvatarStatus.NotFound), null);
            }

            if (copy.Status == ObjectStorageStatus.NotConfigured)
            {
                return (new ProfileAvatarResult(ProfileAvatarStatus.NotConfigured), null);
            }

            if (copy.Status != ObjectStorageStatus.Succeeded)
            {
                return (new ProfileAvatarResult(ProfileAvatarStatus.Unavailable), null);
            }

            return (new ProfileAvatarResult(ProfileAvatarStatus.Saved, entity.AvatarContentType),
                new ProfileAvatarData(buffer.ToArray(), entity.AvatarContentType));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (new ProfileAvatarResult(ProfileAvatarStatus.Unavailable), null);
        }
    }

    public async Task<ProfileAvatarResult> DeleteAsync(string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return new ProfileAvatarResult(ProfileAvatarStatus.NotFound);
        }

        try
        {
            var entity = await _dbContext.UserProfiles
                .FindAsync([userId], cancellationToken)
                .ConfigureAwait(false);
            if (entity?.AvatarObjectId is null)
            {
                return new ProfileAvatarResult(ProfileAvatarStatus.NotFound);
            }

            entity.AvatarObjectId = null;
            entity.AvatarContentType = null;
            entity.UpdatedAtUtc = _timeProvider.GetUtcNow();
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ProfileAvatarResult(ProfileAvatarStatus.Deleted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ProfileAvatarResult(ProfileAvatarStatus.Unavailable);
        }
    }

    public async Task<bool> HasAvatarAsync(string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        try
        {
            var entity = await _dbContext.UserProfiles
                .FindAsync([userId], cancellationToken)
                .ConfigureAwait(false);
            return entity?.AvatarObjectId.HasValue == true &&
                !string.IsNullOrWhiteSpace(entity.AvatarContentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadCappedAsync(Stream content, long cap, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await content.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > cap)
            {
                break;
            }

            buffer.Write(chunk.AsSpan(0, read));
        }

        return total > cap ? new byte[cap + 1] : buffer.ToArray();
    }

    private static bool MatchesSignature(string contentType, byte[] bytes)
    {
        var signature = Signatures[contentType];
        if (bytes.Length < signature.Length + (contentType == "image/webp" ? 12 : 0))
        {
            return false;
        }

        for (var index = 0; index < signature.Length; index++)
        {
            if (bytes[index] != signature[index])
            {
                return false;
            }
        }

        if (contentType == "image/webp")
        {
            return bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50;
        }

        return true;
    }
}
