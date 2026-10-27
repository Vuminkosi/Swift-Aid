using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Notifications;
using SwiftAid.Contracts.Notifications;

namespace SwiftAid.Infrastructure.Persistence.Notifications;

/// <summary>Protects push credentials at rest and scopes registration to the validated actor.</summary>
public sealed class PostgresPushDeviceStore : IPushDeviceStore
{
    private const int MaximumTokenLength = 4096;
    private const string ProtectionPurpose = "SwiftAid.PushDeviceToken.v1";

    private readonly SwiftAidDbContext _dbContext;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;

    public PostgresPushDeviceStore(
        SwiftAidDbContext dbContext,
        IDataProtectionProvider protectionProvider,
        TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        ArgumentNullException.ThrowIfNull(protectionProvider);
        _protector = protectionProvider.CreateProtector(ProtectionPurpose);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<PushDeviceRegistrationStatus> RegisterAsync(
        ActorContext actor,
        PushDeviceRegistrationRequest request,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (request is null ||
            request.InstallationId == Guid.Empty ||
            !Enum.IsDefined(request.Platform) ||
            string.IsNullOrWhiteSpace(request.Token) ||
            request.Token.Length > MaximumTokenLength ||
            !string.Equals(request.Token, request.Token.Trim(), StringComparison.Ordinal))
        {
            return PushDeviceRegistrationStatus.Invalid;
        }

        var fingerprint = Fingerprint(request.Token);
        var protectedToken = Encoding.UTF8.GetBytes(_protector.Protect(request.Token));

        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Serialize both installation refreshes and attempts to register the same token on
        // multiple installations. Database uniqueness remains the final invariant guard.
        var lockKeys = new[]
            {
                ActorLockKey(actor.ActorId),
                $"device:{actor.ActorId.Length}:{actor.ActorId}:{request.InstallationId:N}",
                $"token:{Convert.ToHexString(fingerprint)}"
            }
            .OrderBy(value => value, StringComparer.Ordinal);
        foreach (var lockKey in lockKeys)
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!await IsCurrentActorAsync(
                actor,
                _timeProvider.GetUtcNow().ToUniversalTime(),
                cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return PushDeviceRegistrationStatus.NotAuthenticated;
        }

        var devices = _dbContext.Set<PushDeviceEntity>();
        var installation = await devices.SingleOrDefaultAsync(
                device => device.ActorId == actor.ActorId && device.InstallationId == request.InstallationId,
                cancellationToken)
            .ConfigureAwait(false);
        var tokenOwner = await devices.SingleOrDefaultAsync(
                device => device.TokenFingerprint == fingerprint,
                cancellationToken)
            .ConfigureAwait(false);

        if (installation is not null && tokenOwner is not null && installation.DeviceId != tokenOwner.DeviceId)
        {
            // Remove the old token owner inside the transaction before assigning its unique
            // fingerprint to this installation. The transaction preserves atomic visibility.
            devices.Remove(tokenOwner);
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            tokenOwner = null;
        }

        var target = installation ?? tokenOwner;
        if (target is null)
        {
            target = new PushDeviceEntity
            {
                DeviceId = Guid.NewGuid(),
                ActorId = actor.ActorId,
                InstallationId = request.InstallationId,
                ProtectedToken = protectedToken,
                TokenFingerprint = fingerprint,
                CreatedAtUtc = nowUtc.ToUniversalTime()
            };
            devices.Add(target);
        }
        else
        {
            target.ActorId = actor.ActorId;
            target.InstallationId = request.InstallationId;
        }

        target.Platform = request.Platform;
        target.SoundEnabled = request.SoundEnabled;
        target.ProtectedToken = protectedToken;
        target.TokenFingerprint = fingerprint;
        target.UpdatedAtUtc = nowUtc.ToUniversalTime();

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PushDeviceRegistrationStatus.Registered;
    }

    public async ValueTask<PushDeviceRemovalStatus> RevokeAsync(
        ActorContext actor,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (installationId == Guid.Empty)
        {
            return PushDeviceRemovalStatus.NotFoundOrNotAuthorized;
        }

        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await LockAsync(ActorLockKey(actor.ActorId), cancellationToken).ConfigureAwait(false);
        await LockAsync($"device:{actor.ActorId.Length}:{actor.ActorId}:{installationId:N}", cancellationToken)
            .ConfigureAwait(false);

        if (!await IsCurrentActorAsync(actor, _timeProvider.GetUtcNow().ToUniversalTime(), cancellationToken)
                .ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return PushDeviceRemovalStatus.NotAuthenticated;
        }

        var device = await _dbContext.Set<PushDeviceEntity>()
            .SingleOrDefaultAsync(
                candidate => candidate.ActorId == actor.ActorId &&
                              candidate.InstallationId == installationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (device is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return PushDeviceRemovalStatus.NotFoundOrNotAuthorized;
        }

        _dbContext.Remove(device);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PushDeviceRemovalStatus.Removed;
    }

    public async ValueTask<PushDeviceRemovalStatus> RevokeAllAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await LockAsync(ActorLockKey(actor.ActorId), cancellationToken).ConfigureAwait(false);

        if (!await IsCurrentActorAsync(actor, _timeProvider.GetUtcNow().ToUniversalTime(), cancellationToken)
                .ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return PushDeviceRemovalStatus.NotAuthenticated;
        }

        await _dbContext.Set<PushDeviceEntity>()
            .Where(device => device.ActorId == actor.ActorId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PushDeviceRemovalStatus.Removed;
    }

    public async ValueTask<IReadOnlyList<PushDeviceForDelivery>> GetActiveForRecipientAsync(
        string recipientActorId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientActorId);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var actorIsActive = await (
                from actor in _dbContext.Actors.AsNoTracking()
                join access in _dbContext.ActorAccessStates.AsNoTracking()
                    on actor.ActorId equals access.ActorId
                where actor.ActorId == recipientActorId &&
                      actor.Status == PersistentActorStatus.Active &&
                      access.AccessEpoch >= 0 &&
                      (actor.ExpiresAtUtc == null || actor.ExpiresAtUtc > now)
                select actor.ActorId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!actorIsActive)
        {
            return Array.Empty<PushDeviceForDelivery>();
        }

        var devices = await _dbContext.Set<PushDeviceEntity>()
            .AsNoTracking()
            .Where(device => device.ActorId == recipientActorId)
            .OrderBy(device => device.InstallationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return devices.Select(device => new PushDeviceForDelivery(
                device.InstallationId,
                device.Platform,
                device.SoundEnabled,
                _protector.Unprotect(Encoding.UTF8.GetString(device.ProtectedToken))))
            .ToArray();
    }

    private static byte[] Fingerprint(string token)
    {
        var plaintext = Encoding.UTF8.GetBytes(token);
        try
        {
            // FCM tokens are high-entropy opaque credentials; SHA-256 is used only for equality
            // deduplication, while the recoverable credential is stored through Data Protection.
            return SHA256.HashData(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private Task<int> LockAsync(string lockKey, CancellationToken cancellationToken) =>
        _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);

    private static string ActorLockKey(string actorId) => $"actor:{actorId.Length}:{actorId}";

    private async ValueTask<bool> IsCurrentActorAsync(
        ActorContext actor,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var currentActor = await _dbContext.Actors
            .FromSqlInterpolated($"SELECT * FROM actors WHERE actor_id = {actor.ActorId} FOR SHARE")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var currentAccess = await _dbContext.ActorAccessStates
            .FromSqlInterpolated($"SELECT * FROM actor_access_states WHERE actor_id = {actor.ActorId} FOR SHARE")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return currentActor is { Status: PersistentActorStatus.Active } &&
            (currentActor.ExpiresAtUtc is null || currentActor.ExpiresAtUtc > nowUtc) &&
            currentAccess is not null &&
            currentAccess.AccessEpoch == actor.AccessEpoch;
    }
}
