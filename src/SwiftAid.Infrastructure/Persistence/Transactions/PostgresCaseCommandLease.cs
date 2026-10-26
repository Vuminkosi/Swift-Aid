using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore.Storage;
using SwiftAid.Application.Cases.Commands;

namespace SwiftAid.Infrastructure.Persistence.Transactions;

internal sealed class PostgresCaseCommandLease : ICaseCommandLease
{
    private readonly PostgresCaseCommandUnitOfWork _owner;
    private readonly IDbContextTransaction _transaction;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _active = 1;

    public PostgresCaseCommandLease(
        PostgresCaseCommandUnitOfWork owner,
        SwiftAidDbContext dbContext,
        IDbContextTransaction transaction,
        string primaryActorId,
        Guid caseId,
        CaseLeaseMode mode,
        ImmutableArray<string> lockedActorIds)
    {
        _owner = owner;
        DbContext = dbContext;
        _transaction = transaction;
        PrimaryActorId = primaryActorId;
        CaseId = caseId;
        Mode = mode;
        LockedActorIds = lockedActorIds;
    }

    public Guid CaseId { get; }

    public CaseLeaseMode Mode { get; }

    public ImmutableArray<string> LockedActorIds { get; }

    public bool IsActive => Volatile.Read(ref _active) == 1;

    internal SwiftAidDbContext DbContext { get; }

    internal string PrimaryActorId { get; }

    internal IDbContextTransaction Transaction => _transaction;

    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(!IsActive, this);
            await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
            _owner.Release(this);
        }
    }
}
