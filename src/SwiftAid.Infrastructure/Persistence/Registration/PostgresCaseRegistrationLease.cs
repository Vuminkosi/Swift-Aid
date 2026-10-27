using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Infrastructure.Persistence.Processing;

namespace SwiftAid.Infrastructure.Persistence.Registration;

/// <summary>A registration transaction lease. Replay leases bind one existing case; create leases bind one workspace.</summary>
public sealed class PostgresCaseRegistrationLease : ICaseRegistrationLease
{
    private readonly PostgresCaseRegistrationUnitOfWork _owner;
    private readonly IDbContextTransaction _transaction;
    private readonly IReadOnlySet<object> _preexistingTrackedEntities;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _active = 1;

    public PostgresCaseRegistrationLease(
        PostgresCaseRegistrationUnitOfWork owner,
        SwiftAidDbContext dbContext,
        IDbContextTransaction transaction,
        string primaryActorId,
        string workspaceId,
        Guid? caseId,
        CaseRegistrationLeaseMode mode,
        ImmutableArray<string> lockedActorIds,
        IReadOnlySet<object> preexistingTrackedEntities)
    {
        _owner = owner;
        DbContext = dbContext;
        _transaction = transaction;
        PrimaryActorId = primaryActorId;
        WorkspaceId = workspaceId;
        CaseId = caseId;
        Mode = mode;
        LockedActorIds = lockedActorIds;
        _preexistingTrackedEntities = preexistingTrackedEntities;
    }

    public Guid? CaseId { get; }

    public CaseRegistrationLeaseMode Mode { get; }

    public ImmutableArray<string> LockedActorIds { get; }

    public bool IsActive => Volatile.Read(ref _active) == 1;

    internal SwiftAidDbContext DbContext { get; }

    internal string PrimaryActorId { get; }

    internal string WorkspaceId { get; }

    internal IDbContextTransaction Transaction => _transaction;

    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(!IsActive, this);
            _owner.RejectChangesToPreexistingEntities(_preexistingTrackedEntities);
            try
            {
                await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (PostgresCaseCommandErrorMapper.TryMap(exception, out _))
            {
                _ = PostgresCaseCommandErrorMapper.TryMap(exception, out var mapped);
                throw mapped!;
            }

            await CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsActive)
            {
                return;
            }

            try
            {
                await _transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await CompleteAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask CompleteAsync()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
        {
            return;
        }

        try
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _owner.ClearLeaseOwnedChanges(_preexistingTrackedEntities);
            }
            finally
            {
                _owner.Release(this);
            }
        }
    }
}
