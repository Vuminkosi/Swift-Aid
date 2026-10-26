using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;

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
            if (CurrentLease is not null || _dbContext.Database.CurrentTransaction is not null)
            {
                throw new InvalidOperationException("A case lease or database transaction is already active for this scoped context.");
            }

            var transaction = await _dbContext.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                const string authorityLockClause = "FOR SHARE";
                var caseLockClause = mode == CaseLeaseMode.Write ? "FOR UPDATE" : "FOR SHARE";
                var initial = await ReadCaseSeedAsync(caseId, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The managed case does not exist.");

                var actorIds = NormalizeActorHints(actor, initial.AccountableOwnerActorId, additionalActorHints);
                await LockSingleRowAsync(
                    $"SELECT organization_id FROM public.organizations WHERE organization_id = @organization_id ORDER BY organization_id {authorityLockClause}",
                    "organization_id", initial.OrganizationId, cancellationToken).ConfigureAwait(false);
                await LockSingleRowAsync(
                    $"SELECT workspace_id FROM public.workspaces WHERE workspace_id = @workspace_id ORDER BY workspace_id {authorityLockClause}",
                    "workspace_id", initial.WorkspaceId, cancellationToken).ConfigureAwait(false);

                var lockedAccessActors = await LockTextRowsAsync(
                    $"SELECT actor_id FROM public.actor_access_states WHERE actor_id = ANY (@actor_ids) ORDER BY actor_id {authorityLockClause}",
                    "actor_ids", actorIds.ToArray(), cancellationToken).ConfigureAwait(false);
                RequireAllActorsLocked(actorIds, lockedAccessActors, "actor access state");

                var lockedActors = await LockTextRowsAsync(
                    $"SELECT actor_id FROM public.actors WHERE actor_id = ANY (@actor_ids) ORDER BY actor_id {authorityLockClause}",
                    "actor_ids", actorIds.ToArray(), cancellationToken).ConfigureAwait(false);
                RequireAllActorsLocked(actorIds, lockedActors, "actor");

                await LockTextRowsAsync(
                    $"SELECT actor_id FROM public.organization_memberships WHERE organization_id = @organization_id AND actor_id = ANY (@actor_ids) ORDER BY actor_id, membership_id {authorityLockClause}",
                    "organization_id", initial.OrganizationId, "actor_ids", actorIds.ToArray(), cancellationToken).ConfigureAwait(false);

                var grantGroupIds = await LockTextRowsAsync(
                    $"SELECT grant_group_id FROM public.access_grant_groups WHERE organization_id = @organization_id AND actor_id = ANY (@actor_ids) ORDER BY grant_group_id {authorityLockClause}",
                    "organization_id", initial.OrganizationId, "actor_ids", actorIds.ToArray(), cancellationToken).ConfigureAwait(false);
                if (grantGroupIds.Count > 0)
                {
                    await LockTextRowsAsync(
                        $"SELECT grant_group_id FROM public.access_grant_group_regions WHERE grant_group_id = ANY (@grant_group_ids) ORDER BY grant_group_id, region_id {authorityLockClause}",
                        "grant_group_ids", grantGroupIds.ToArray(), cancellationToken).ConfigureAwait(false);
                    await LockTextRowsAsync(
                        $"SELECT grant_group_id FROM public.access_grant_group_operations WHERE grant_group_id = ANY (@grant_group_ids) ORDER BY grant_group_id, operation {authorityLockClause}",
                        "grant_group_ids", grantGroupIds.ToArray(), cancellationToken).ConfigureAwait(false);
                }

                await LockTextRowsAsync(
                    $"SELECT actor_id FROM public.case_shares WHERE case_id = @case_id AND actor_id = ANY (@actor_ids) ORDER BY actor_id {authorityLockClause}",
                    "case_id", caseId, "actor_ids", actorIds.ToArray(), cancellationToken).ConfigureAwait(false);

                var current = await LockCaseAndReadAsync(caseId, caseLockClause, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The managed case does not exist.");

                if (!string.Equals(initial.OrganizationId, current.OrganizationId, StringComparison.Ordinal) ||
                    !string.Equals(initial.WorkspaceId, current.WorkspaceId, StringComparison.Ordinal))
                {
                    throw CreateDiscoveryChanged(caseId, actor.ActorId, additionalActorHints, current.AccountableOwnerActorId);
                }

                if (!actorIds.Contains(current.AccountableOwnerActorId, StringComparer.Ordinal))
                {
                    throw CreateDiscoveryChanged(caseId, actor.ActorId, additionalActorHints, current.AccountableOwnerActorId);
                }

                await LockTextRowsAsync(
                    $"SELECT region_id FROM public.managed_case_regions WHERE case_id = @case_id ORDER BY region_id {authorityLockClause}",
                    "case_id", caseId, cancellationToken).ConfigureAwait(false);

                var lease = new PostgresCaseCommandLease(this, _dbContext, transaction, actor.ActorId, caseId, mode, actorIds);
                _currentLease = lease;
                return lease;
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
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

    private async Task<CaseSeed?> ReadCaseSeedAsync(Guid caseId, CancellationToken cancellationToken) =>
        await _dbContext.ManagedCases
            .AsNoTracking()
            .Where(entity => entity.CaseId == caseId)
            .Select(entity => new CaseSeed(entity.OrganizationId, entity.WorkspaceId, entity.AccountableOwnerActorId))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private async Task<CaseSeed?> LockCaseAndReadAsync(
        Guid caseId,
        string caseLockClause,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            $"SELECT organization_id, workspace_id, accountable_owner_actor_id FROM public.managed_cases WHERE case_id = @case_id {caseLockClause}",
            ("case_id", caseId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new CaseSeed(reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private async Task LockSingleRowAsync(
        string sql,
        string parameterName,
        string parameterValue,
        CancellationToken cancellationToken)
    {
        var rows = await LockTextRowsAsync(sql, parameterName, parameterValue, cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1)
        {
            throw new InvalidOperationException("The case authority scope is unavailable.");
        }
    }

    private async Task<List<string>> LockTextRowsAsync(
        string sql,
        string parameterName,
        object parameterValue,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql, (parameterName, parameterValue));
        return await ReadTextRowsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<string>> LockTextRowsAsync(
        string sql,
        string firstParameterName,
        object firstParameterValue,
        string secondParameterName,
        object secondParameterValue,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql,
            (firstParameterName, firstParameterValue),
            (secondParameterName, secondParameterValue));
        return await ReadTextRowsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<string>> ReadTextRowsAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private DbCommand CreateCommand(string sql, params (string Name, object Value)[] parameters)
    {
        var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = _dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("A PostgreSQL transaction is required for case authority access.");
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
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

    private static void RequireAllActorsLocked(
        ImmutableArray<string> requestedActorIds,
        IReadOnlyCollection<string> lockedActorIds,
        string rowType)
    {
        if (requestedActorIds.Any(actorId => !lockedActorIds.Contains(actorId, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException($"The required {rowType} authority row is unavailable.");
        }
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

    private sealed record CaseSeed(string OrganizationId, string WorkspaceId, string AccountableOwnerActorId);
}
