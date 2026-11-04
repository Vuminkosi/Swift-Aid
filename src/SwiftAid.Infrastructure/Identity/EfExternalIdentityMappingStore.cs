using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Identity;

public sealed class ExternalIdentityPersistenceException : Exception
{
    public ExternalIdentityPersistenceException() : base("External identity persistence is unavailable.")
    {
    }
}

/// <summary>
/// Resolves the exact provider/issuer/subject key and, on first use, atomically creates
/// an ordinary actor, an Identity user keyed by that actor ID, and the mapping row.
/// </summary>
public sealed class EfExternalIdentityMappingStore(
    SwiftAidDbContext dbContext,
    UserManager<SwiftAidUser> userManager,
    TimeProvider timeProvider,
    OrganizationSsoRegistry organizationSsoRegistry) : IExternalIdentityMappingStore
{
    private const string MappingUniqueConstraint = "ux_external_identities_provider_issuer_subject";

    public async ValueTask<ExternalIdentityResolution> ResolveOrCreateOrdinaryActorAsync(
        string provider,
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        if (!IsValid(provider, 128) || !IsValid(issuer, 2048) || !IsValid(subject, 1024))
        {
            return new ExternalIdentityResolution(ExternalIdentityResolutionStatus.Rejected);
        }

        var existing = await FindMappingAsync(provider, issuer, subject, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return await ResolveExistingActorAsync(existing.ActorId, cancellationToken).ConfigureAwait(false);
        }

        var existingEntities = dbContext.ChangeTracker.Entries()
            .Select(entry => entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            existing = await FindMappingAsync(provider, issuer, subject, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                DetachAttemptEntries(existingEntities);
                return await ResolveExistingActorAsync(existing.ActorId, cancellationToken).ConfigureAwait(false);
            }

            var now = timeProvider.GetUtcNow();
            var actorId = Guid.NewGuid().ToString("N");
            var actor = new ActorEntity
            {
                ActorId = actorId,
                Status = PersistentActorStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            dbContext.Actors.Add(actor);
            dbContext.ActorAccessStates.Add(new ActorAccessStateEntity
            {
                ActorId = actorId,
                AccessEpoch = 0,
                Version = 1,
                UpdatedAtUtc = now
            });

            var user = new SwiftAidUser
            {
                Id = actorId,
                UserName = actorId,
                NormalizedUserName = actorId.ToUpperInvariant(),
                Email = null,
                NormalizedEmail = null,
                EmailConfirmed = false,
                LockoutEnabled = true
            };
            var identityResult = await userManager.CreateAsync(user).ConfigureAwait(false);
            if (!identityResult.Succeeded)
            {
                throw new ExternalIdentityPersistenceException();
            }

            dbContext.ExternalIdentityMappings.Add(new ExternalIdentityMappingEntity
            {
                MappingId = Guid.NewGuid(),
                ActorId = actorId,
                Provider = provider,
                Issuer = issuer,
                Subject = subject,
                CreatedAtUtc = now
            });

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ExternalIdentityResolution(ExternalIdentityResolutionStatus.Resolved, actorId, Created: true);
        }
        catch (DbUpdateException exception) when (IsMappingConflict(exception))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            DetachAttemptEntries(existingEntities);
            var racedMapping = await FindMappingAsync(provider, issuer, subject, cancellationToken).ConfigureAwait(false);
            return racedMapping is null
                ? throw new ExternalIdentityPersistenceException()
                : await ResolveExistingActorAsync(racedMapping.ActorId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            DetachAttemptEntries(existingEntities);
            throw;
        }
    }

    public async ValueTask<ExternalIdentityResolution> ResolveExistingAsync(
        string provider,
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        if (!IsValid(provider, 128) || !IsValid(issuer, 2048) || !IsValid(subject, 1024))
        {
            return new ExternalIdentityResolution(ExternalIdentityResolutionStatus.Rejected);
        }

        var mapping = await FindMappingAsync(provider, issuer, subject, cancellationToken).ConfigureAwait(false);
        return mapping is null
            ? new ExternalIdentityResolution(ExternalIdentityResolutionStatus.Rejected)
            : await ResolveExistingActorAsync(mapping.ActorId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ExternalIdentityLinkStatus> LinkAsync(
        string actorId,
        string provider,
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        if (!IsValid(actorId, 128) || !IsValid(provider, 128) ||
            !IsValid(issuer, 2048) || !IsValid(subject, 1024))
        {
            return ExternalIdentityLinkStatus.Rejected;
        }

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var actor = await LockActorAsync(actorId, cancellationToken).ConfigureAwait(false);
        if (actor is null || actor.Status != PersistentActorStatus.Active ||
            actor.ExpiresAtUtc is { } expiry && expiry <= timeProvider.GetUtcNow())
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ExternalIdentityLinkStatus.ActorUnavailable;
        }

        var existing = await FindMappingAsync(provider, issuer, subject, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return string.Equals(existing.ActorId, actorId, StringComparison.Ordinal)
                ? ExternalIdentityLinkStatus.AlreadyLinked
                : ExternalIdentityLinkStatus.Conflict;
        }

        var mapping = new ExternalIdentityMappingEntity
        {
            MappingId = Guid.NewGuid(),
            ActorId = actorId,
            Provider = provider,
            Issuer = issuer,
            Subject = subject,
            CreatedAtUtc = timeProvider.GetUtcNow()
        };
        dbContext.ExternalIdentityMappings.Add(mapping);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ExternalIdentityLinkStatus.Linked;
        }
        catch (DbUpdateException exception) when (IsMappingConflict(exception))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            dbContext.Entry(mapping).State = EntityState.Detached;

            var racedMapping = await FindMappingAsync(provider, issuer, subject, cancellationToken)
                .ConfigureAwait(false);
            return racedMapping is null
                ? ExternalIdentityLinkStatus.Conflict
                : string.Equals(racedMapping.ActorId, actorId, StringComparison.Ordinal)
                    ? ExternalIdentityLinkStatus.AlreadyLinked
                    : ExternalIdentityLinkStatus.Conflict;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.Entry(mapping).State = EntityState.Detached;
            throw;
        }
    }

    public async ValueTask<ExternalIdentityUnlinkStatus> UnlinkProviderAsync(
        string actorId,
        string provider,
        CancellationToken cancellationToken)
    {
        if (!IsValid(actorId, 128) || !IsValid(provider, 128))
        {
            return ExternalIdentityUnlinkStatus.Rejected;
        }

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var actor = await LockActorAsync(actorId, cancellationToken).ConfigureAwait(false);
        if (actor is null || actor.Status != PersistentActorStatus.Active ||
            actor.ExpiresAtUtc is { } expiry && expiry <= timeProvider.GetUtcNow())
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ExternalIdentityUnlinkStatus.ActorUnavailable;
        }

        var mappings = await dbContext.ExternalIdentityMappings
            .Where(mapping => mapping.ActorId == actorId && mapping.Provider == provider)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (mappings.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ExternalIdentityUnlinkStatus.NotLinked;
        }

        var alternateMappings = await dbContext.ExternalIdentityMappings
            .AsNoTracking()
            .Where(mapping => mapping.ActorId == actorId && mapping.Provider != provider)
            .Select(mapping => new { mapping.Provider, mapping.Issuer })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasAnotherLoginRoute = alternateMappings.Any(mapping =>
            organizationSsoRegistry.IsUsableLoginRoute(mapping.Provider, mapping.Issuer));
        if (!hasAnotherLoginRoute)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ExternalIdentityUnlinkStatus.LastLoginRoute;
        }

        dbContext.ExternalIdentityMappings.RemoveRange(mappings);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ExternalIdentityUnlinkStatus.Unlinked;
    }

    private async Task<ActorEntity?> LockActorAsync(
        string actorId,
        CancellationToken cancellationToken) =>
        await dbContext.Actors
            .FromSqlInterpolated($"SELECT * FROM public.actors WHERE actor_id = {actorId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private async Task<ExternalIdentityMappingEntity?> FindMappingAsync(
        string provider,
        string issuer,
        string subject,
        CancellationToken cancellationToken) =>
        await dbContext.ExternalIdentityMappings.AsNoTracking()
            .SingleOrDefaultAsync(
                mapping => mapping.Provider == provider && mapping.Issuer == issuer && mapping.Subject == subject,
                cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<ExternalIdentityResolution> ResolveExistingActorAsync(
        string actorId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var actor = await dbContext.Actors.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.ActorId == actorId, cancellationToken)
            .ConfigureAwait(false);
        if (actor is null || actor.Status != PersistentActorStatus.Active ||
            actor.ExpiresAtUtc is { } expiry && expiry <= now)
        {
            return new ExternalIdentityResolution(ExternalIdentityResolutionStatus.Rejected);
        }

        var accessStateExists = await dbContext.ActorAccessStates.AsNoTracking()
            .AnyAsync(state => state.ActorId == actorId && state.AccessEpoch >= 0, cancellationToken)
            .ConfigureAwait(false);
        var user = accessStateExists
            ? await userManager.FindByIdAsync(actorId).ConfigureAwait(false)
            : null;
        return user is null
            ? new ExternalIdentityResolution(ExternalIdentityResolutionStatus.Rejected)
            : new ExternalIdentityResolution(ExternalIdentityResolutionStatus.Resolved, actorId);
    }

    private static bool IsValid(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maxLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsMappingConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: MappingUniqueConstraint
        };

    private void DetachAttemptEntries(HashSet<object> existingEntities)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToArray())
        {
            if (!existingEntities.Contains(entry.Entity))
            {
                entry.State = EntityState.Detached;
            }
        }
    }
}
