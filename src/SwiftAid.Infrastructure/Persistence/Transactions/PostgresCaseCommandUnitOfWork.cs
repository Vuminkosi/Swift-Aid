using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Infrastructure.Persistence.Processing;

namespace SwiftAid.Infrastructure.Persistence.Transactions;

/// <summary>Obtains a deterministic PostgreSQL row-lock lease for one managed case.</summary>
public sealed class PostgresCaseCommandUnitOfWork : ICaseCommandUnitOfWork, ICaseCommandLeaseAccessor
{
    private readonly SwiftAidDbContext _dbContext;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PostgresCaseCommandLease? _currentLease;

    public PostgresCaseCommandUnitOfWork(SwiftAidDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public ICaseCommandLease? CurrentLease => _currentLease is { IsActive: true } lease ? lease : null;

    public async ValueTask<ICaseCommandLease> BeginCaseAsync(
        ActorContext actor,
        Guid caseId,
        ImmutableArray<string> additionalActorHints,
        CaseLeaseMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("A case identifier is required.", nameof(caseId));
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_currentLease is not null || _dbContext.Database.CurrentTransaction is not null)
            {
                throw new InvalidOperationException("A case lease or database transaction is already active for this scoped context.");
            }

            RejectPendingChanges();
            var preexistingTrackedEntities = _dbContext.ChangeTracker.Entries()
                .Select(entry => entry.Entity)
                .ToHashSet(ReferenceEqualityComparer.Instance);

            var transaction = await _dbContext.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var initialOwnerActorId = await ReadCurrentOwnerActorIdAsync(caseId, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The managed case does not exist.");

                var actorIds = NormalizeActorHints(actor, initialOwnerActorId, additionalActorHints);
                var lockResult = await AcquireCaseAuthorityLockAsync(
                    transaction, actor.ActorId, caseId, actorIds, mode, cancellationToken).ConfigureAwait(false);
                switch (lockResult)
                {
                    case 0:
                        break;
                    case 1:
                    {
                        var discoveredOwner = await ReadCurrentOwnerActorIdAsync(caseId, cancellationToken)
                            .ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(discoveredOwner) ||
                            !string.Equals(discoveredOwner, discoveredOwner.Trim(), StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("The current case authority scope is unavailable.");
                        }

                        throw CreateDiscoveryChanged(caseId, actor.ActorId, additionalActorHints, discoveredOwner);
                    }
                    case 2:
                        throw new InvalidOperationException("The case authority scope is unavailable.");
                    case 3:
                        throw new InvalidOperationException("The case authority lock request is invalid.");
                    default:
                        throw new InvalidOperationException("The case authority lock helper returned an unsupported result.");
                }

                var lease = new PostgresCaseCommandLease(
                    this, _dbContext, transaction, actor.ActorId, caseId, mode, actorIds, preexistingTrackedEntities);
                _currentLease = lease;
                return lease;
            }
            catch (Exception exception)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }

                if (PostgresCaseCommandErrorMapper.TryMap(exception, out var mapped))
                {
                    throw mapped!;
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void Release(PostgresCaseCommandLease lease)
    {
        if (ReferenceEquals(_currentLease, lease))
        {
            _currentLease = null;
        }
    }

    internal void ClearLeaseOwnedChanges(IReadOnlySet<object> preexistingTrackedEntities)
    {
        foreach (var entry in _dbContext.ChangeTracker.Entries()
                     .Where(entry => !preexistingTrackedEntities.Contains(entry.Entity)))
        {
            entry.State = EntityState.Detached;
        }
    }

    internal void RejectChangesToPreexistingEntities(IReadOnlySet<object> preexistingTrackedEntities)
    {
        foreach (var entry in _dbContext.ChangeTracker.Entries())
        {
            if (preexistingTrackedEntities.Contains(entry.Entity) &&
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("A case command lease cannot persist changes to pre-existing tracked entities.");
            }
        }
    }

    private void RejectPendingChanges()
    {
        if (_dbContext.ChangeTracker.Entries()
            .Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("A case lease cannot begin with pending changes in its scoped context.");
        }
    }

    private async Task<string?> ReadCurrentOwnerActorIdAsync(Guid caseId, CancellationToken cancellationToken) =>
        await _dbContext.ManagedCases
            .AsNoTracking()
            .Where(entity => entity.CaseId == caseId)
            .Select(entity => entity.AccountableOwnerActorId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private async Task<short> AcquireCaseAuthorityLockAsync(
        IDbContextTransaction transaction,
        string actorId,
        Guid caseId,
        ImmutableArray<string> actorHints,
        CaseLeaseMode mode,
        CancellationToken cancellationToken)
    {
        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT swiftaid_internal.lock_case_authority(@case_id, @actor_id, @actor_hints, @write)";
        command.Transaction = transaction.GetDbTransaction();
        command.Parameters.Add(new NpgsqlParameter("case_id", NpgsqlDbType.Uuid) { Value = caseId });
        command.Parameters.Add(new NpgsqlParameter("actor_id", NpgsqlDbType.Text) { Value = actorId });
        command.Parameters.Add(new NpgsqlParameter("actor_hints", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = actorHints.ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter("write", NpgsqlDbType.Boolean)
        {
            Value = mode == CaseLeaseMode.Write
        });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is short status
            ? status
            : throw new InvalidOperationException("The case authority lock helper returned an invalid result.");
    }

    private static ImmutableArray<string> NormalizeActorHints(
        ActorContext actor,
        string accountableOwnerActorId,
        ImmutableArray<string> additionalActorHints)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        builder.Add(actor.ActorId);
        builder.Add(accountableOwnerActorId);
        if (!additionalActorHints.IsDefault)
        {
            foreach (var hint in additionalActorHints)
            {
                if (string.IsNullOrWhiteSpace(hint) || !string.Equals(hint, hint.Trim(), StringComparison.Ordinal))
                {
                    throw new ArgumentException("Actor hints must be non-empty canonical identifiers.", nameof(additionalActorHints));
                }

                builder.Add(hint);
            }
        }

        return builder.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private static CaseLeaseDiscoveryChangedException CreateDiscoveryChanged(
        Guid caseId,
        string actorId,
        ImmutableArray<string> additionalActorHints,
        string discoveredOwnerActorId)
    {
        ImmutableArray<string> hints = additionalActorHints.IsDefault
            ? ImmutableArray<string>.Empty
            : additionalActorHints;
        if (!string.Equals(discoveredOwnerActorId, actorId, StringComparison.Ordinal))
        {
            hints = hints.Add(discoveredOwnerActorId);
        }

        return new CaseLeaseDiscoveryChangedException(caseId, hints);
    }
}
