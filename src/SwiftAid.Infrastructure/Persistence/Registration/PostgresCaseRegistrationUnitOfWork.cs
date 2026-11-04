using System.Collections.Immutable;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Infrastructure.Persistence.Processing;

namespace SwiftAid.Infrastructure.Persistence.Registration;

/// <summary>Obtains a deterministic PostgreSQL transaction lease for managed-case registration.</summary>
/// <remarks>
/// Create leases serialize concurrent registrations per (workspace, actor) with a transaction-scoped
/// advisory lock; replay leases reuse the case-authority row lock. The scoped context holds at most
/// one active lease, mirroring the command unit of work.
/// </remarks>
public sealed class PostgresCaseRegistrationUnitOfWork : ICaseRegistrationUnitOfWork
{
    private readonly SwiftAidDbContext _dbContext;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PostgresCaseRegistrationLease? _currentLease;

    public PostgresCaseRegistrationUnitOfWork(SwiftAidDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async ValueTask<ICaseRegistrationLease> BeginCreateAsync(
        ActorContext actor,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireCanonicalActor(actor.ActorId);
        if (string.IsNullOrWhiteSpace(workspaceId) || !string.Equals(workspaceId, workspaceId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical workspace identifier is required.", nameof(workspaceId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RejectActiveLease();
            RejectPendingChanges();
            var preexistingTrackedEntities = CaptureTrackedEntities();

            var transaction = await _dbContext.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await AcquireCreateLockAsync(transaction, workspaceId, actor.ActorId, cancellationToken)
                    .ConfigureAwait(false);

                var lease = new PostgresCaseRegistrationLease(
                    this, _dbContext, transaction, actor.ActorId, workspaceId, null,
                    CaseRegistrationLeaseMode.Create, ImmutableArray.Create(actor.ActorId), preexistingTrackedEntities);
                _currentLease = lease;
                return lease;
            }
            catch (Exception exception)
            {
                await RollbackAsync(transaction).ConfigureAwait(false);
                throw MapOrRethrow(exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ICaseRegistrationLease> BeginExistingCaseReplayAsync(
        ActorContext actor,
        Guid candidateCaseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireCanonicalActor(actor.ActorId);
        if (candidateCaseId == Guid.Empty)
        {
            throw new ArgumentException("A case identifier is required.", nameof(candidateCaseId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RejectActiveLease();
            RejectPendingChanges();
            var preexistingTrackedEntities = CaptureTrackedEntities();

            var transaction = await _dbContext.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var workspaceId = await ReadCaseWorkspaceIdAsync(candidateCaseId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The managed case does not exist.");
                var ownerActorId = await ReadCurrentOwnerActorIdAsync(candidateCaseId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The current case authority scope is unavailable.");

                var actorIds = ImmutableArray.Create(actor.ActorId, ownerActorId)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
                var lockResult = await AcquireCaseAuthorityLockAsync(
                    transaction, actor.ActorId, candidateCaseId, actorIds, cancellationToken).ConfigureAwait(false);
                switch (lockResult)
                {
                    case 0:
                        break;
                    case 1:
                    {
                        var discoveredOwner = await ReadCurrentOwnerActorIdAsync(candidateCaseId, cancellationToken)
                            .ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(discoveredOwner) ||
                            !string.Equals(discoveredOwner, discoveredOwner.Trim(), StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("The current case authority scope is unavailable.");
                        }

                        throw new CaseLeaseDiscoveryChangedException(candidateCaseId, actorIds.Add(discoveredOwner));
                    }
                    case 2:
                        throw new InvalidOperationException("The case authority scope is unavailable.");
                    case 3:
                        throw new InvalidOperationException("The case authority lock request is invalid.");
                    default:
                        throw new InvalidOperationException("The case authority lock helper returned an unsupported result.");
                }

                var lease = new PostgresCaseRegistrationLease(
                    this, _dbContext, transaction, actor.ActorId, workspaceId, candidateCaseId,
                    CaseRegistrationLeaseMode.ExistingCaseReplay, actorIds, preexistingTrackedEntities);
                _currentLease = lease;
                return lease;
            }
            catch (Exception exception)
            {
                await RollbackAsync(transaction).ConfigureAwait(false);
                throw MapOrRethrow(exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void Release(PostgresCaseRegistrationLease lease)
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
                throw new InvalidOperationException("A registration lease cannot persist changes to pre-existing tracked entities.");
            }
        }
    }

    private void RejectActiveLease()
    {
        if (_currentLease is not null || _dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("A registration lease or database transaction is already active for this scoped context.");
        }
    }

    private void RejectPendingChanges()
    {
        if (_dbContext.ChangeTracker.Entries()
            .Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("A registration lease cannot begin with pending changes in its scoped context.");
        }
    }

    private HashSet<object> CaptureTrackedEntities() => _dbContext.ChangeTracker.Entries()
        .Select(entry => entry.Entity)
        .ToHashSet(ReferenceEqualityComparer.Instance);

    private static void RequireCanonicalActor(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId) || !string.Equals(actorId, actorId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical actor identifier is required.", nameof(actorId));
        }
    }

    private async Task AcquireCreateLockAsync(
        IDbContextTransaction transaction,
        string workspaceId,
        string actorId,
        CancellationToken ct)
    {
        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@create_key, 0));";
        command.Parameters.Add(new NpgsqlParameter("create_key", NpgsqlDbType.Text)
        {
            Value = $"swiftaid.registration.create:{workspaceId}:{actorId}"
        });
        await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private async Task<string?> ReadCaseWorkspaceIdAsync(Guid caseId, CancellationToken cancellationToken) =>
        await _dbContext.ManagedCases
            .AsNoTracking()
            .Where(entity => entity.CaseId == caseId)
            .Select(entity => entity.WorkspaceId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

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
        command.Parameters.Add(new NpgsqlParameter("write", NpgsqlDbType.Boolean) { Value = true });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is short status
            ? status
            : throw new InvalidOperationException("The case authority lock helper returned an invalid result.");
    }

    private static async Task RollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Exception MapOrRethrow(Exception exception) =>
        PostgresCaseCommandErrorMapper.TryMap(exception, out var mapped) ? mapped! : exception;
}
