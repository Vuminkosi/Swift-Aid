using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Client.LocalStore;
using SwiftAid.Client.Sync;

namespace SwiftAid.Client.Outbox;

public enum LocalCommandState
{
    Queued,
    Sending,
    RetryScheduled,
    Acknowledged,
    Synchronized,
    BlockedConflict,
    BlockedAuthorization,
    RejectedValidation,
    CancelledByUser
}

public sealed record LocalCommandProposal(
    Guid CommandId,
    Guid CaseId,
    string WorkspaceId,
    string CommandType,
    long BaseAggregateVersion,
    Guid? DependsOnCommandId,
    byte[] ProposalPayload);

public sealed record LocalCommandReceipt(Guid CommandId, Guid CaseId, long AggregateVersion);

public sealed record LocalCommandConflict(
    Guid CommandId,
    Guid CaseId,
    string WorkspaceId,
    long AccessEpoch,
    long BaseAggregateVersion,
    byte[] ProposalPayload,
    byte[] LatestAuthorizedSnapshot,
    long LatestAggregateVersion);

public sealed record OutboundCaseCommand(
    Guid CommandId,
    Guid CaseId,
    string OwnerActorId,
    long AccessEpoch,
    string WorkspaceId,
    string CommandType,
    long ExpectedAggregateVersion,
    byte[] Payload,
    byte[] PayloadHash,
    int AttemptCount);

public sealed record LocalCommandStatus(
    Guid CommandId,
    Guid CaseId,
    LocalCommandState State,
    long BaseAggregateVersion,
    long? AcceptedAggregateVersion,
    Guid? DependsOnCommandId,
    DateTimeOffset? RetryAtUtc);

/// <summary>Durable, staff-only local queue; payloads are opaque to this client store.</summary>
public sealed class CommandOutbox : ICommandOutbox
{
    private const int EncryptionVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly TimeSpan SendingLease = TimeSpan.FromMinutes(2);
    private readonly IDbContextFactory<CommandOutboxDbContext> _contextFactory;
    private readonly ILocalDraftAccessContextProvider _accessContextProvider;
    private readonly ILocalDraftKeyStore _keyStore;
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _jitter;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CommandOutbox(
        IDbContextFactory<CommandOutboxDbContext> contextFactory,
        ILocalDraftAccessContextProvider accessContextProvider,
        ILocalDraftKeyStore keyStore,
        TimeProvider? timeProvider = null,
        Func<double>? jitter = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _accessContextProvider = accessContextProvider ?? throw new ArgumentNullException(nameof(accessContextProvider));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jitter = jitter ?? Random.Shared.NextDouble;
    }

    public async Task EnqueueAsync(LocalCommandProposal proposal, CancellationToken cancellationToken)
    {
        ValidateProposal(proposal);
        var access = await RequireAccessAsync(proposal.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await CreateReadyContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var existing = await db.Commands.SingleOrDefaultAsync(
                item => item.CommandId == proposal.CommandId.ToString("D"), cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.OwnerActorId, access.ActorId, StringComparison.Ordinal) ||
                    existing.AccessEpoch != access.AccessEpoch)
                {
                    throw new LocalCommandIdentityConflictException();
                }

                var existingPayload = await DecryptPayloadAsync(existing, existing.ProposalPayload,
                    existing.ProposalNonce, existing.ProposalTag, "proposal", cancellationToken).ConfigureAwait(false);
                if (!MatchesProposal(existing, proposal, existingPayload))
                {
                    CryptographicOperations.ZeroMemory(existingPayload);
                    throw new LocalCommandIdentityConflictException();
                }

                CryptographicOperations.ZeroMemory(existingPayload);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (proposal.DependsOnCommandId is Guid dependencyId)
            {
                var dependency = await db.Commands.AsNoTracking().SingleOrDefaultAsync(
                    item => item.CommandId == dependencyId.ToString("D") && item.OwnerActorId == access.ActorId &&
                        item.AccessEpoch == access.AccessEpoch, cancellationToken).ConfigureAwait(false);
                if (dependency is null || dependency.CaseId != proposal.CaseId.ToString("D") ||
                    !string.Equals(dependency.WorkspaceId, proposal.WorkspaceId, StringComparison.Ordinal))
                {
                    throw new LocalCommandDependencyException();
                }
            }

            var sequence = await db.Commands
                .Where(item => item.CaseId == proposal.CaseId.ToString("D") &&
                    item.OwnerActorId == access.ActorId && item.AccessEpoch == access.AccessEpoch)
                .Select(item => (long?)item.Sequence)
                .MaxAsync(cancellationToken)
                .ConfigureAwait(false) ?? 0;

            var encryptedProposal = await EncryptPayloadAsync(
                access, proposal.CommandId, proposal.CaseId, proposal.WorkspaceId, "proposal",
                proposal.ProposalPayload, cancellationToken).ConfigureAwait(false);
            db.Commands.Add(new LocalCommandRow
            {
                CommandId = proposal.CommandId.ToString("D"),
                CaseId = proposal.CaseId.ToString("D"),
                WorkspaceId = proposal.WorkspaceId,
                OwnerActorId = access.ActorId,
                AccessEpoch = access.AccessEpoch,
                CommandType = proposal.CommandType,
                Sequence = checked(sequence + 1),
                BaseAggregateVersion = proposal.BaseAggregateVersion,
                DependsOnCommandId = proposal.DependsOnCommandId?.ToString("D"),
                EncryptionVersion = EncryptionVersion,
                ProposalPayload = encryptedProposal.Ciphertext,
                ProposalNonce = encryptedProposal.Nonce,
                ProposalTag = encryptedProposal.Tag,
                State = LocalCommandState.Queued,
                CreatedAtUtc = _timeProvider.GetUtcNow(),
                AttemptCount = 0
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Claims the next ready item for a case. The caller binds the expected version into its payload
    /// before calling FreezeForSendAsync; once frozen, retries always receive the same bytes and hash.
    /// </summary>
    public async Task<bool> IsRefreshedAuthorityCurrentAsync(SyncAccessResult authority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!authority.IsReady || string.IsNullOrWhiteSpace(authority.ActorId) || authority.AccessEpoch < 0 ||
            authority.PermittedWorkspaceIds is null)
        {
            return false;
        }

        var access = await _accessContextProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return IsSameRefreshedAuthority(access, authority);
    }

    public async Task<LocalCommandProposal?> GetNextReadyAsync(
        SyncAccessResult authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
            if (!IsSameRefreshedAuthority(access, authority)) return null;
            await using var db = await CreateReadyContextAsync(cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var permittedWorkspaceIds = access.PermittedWorkspaceIds.ToArray();
            var rows = await db.Commands.Where(item => item.OwnerActorId == access.ActorId &&
                    item.AccessEpoch == access.AccessEpoch &&
                    permittedWorkspaceIds.Contains(item.WorkspaceId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // SQLite cannot translate DateTimeOffset ordering. The query is already limited to the
            // current authorized actor, epoch, and workspaces, so sort those local rows by UTC time.
            rows = rows.OrderBy(item => item.CreatedAtUtc.UtcDateTime.Ticks)
                .ThenBy(item => item.Sequence)
                .ThenBy(item => item.CommandId, StringComparer.Ordinal)
                .ToList();

            foreach (var row in rows)
            {
                if (!CanSend(row, rows, now))
                {
                    continue;
                }

                row.State = LocalCommandState.Sending;
                row.LeaseUntilUtc = now.Add(SendingLease);
                row.NextAttemptAtUtc = null;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                var expectedVersion = GetExpectedVersion(row, rows);
                return await ToProposalAsync(row, expectedVersion, cancellationToken).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OutboundCaseCommand> FreezeForSendAsync(
        Guid commandId,
        long expectedAggregateVersion,
        byte[] canonicalPayload,
        SyncAccessResult authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(canonicalPayload);
        if (commandId == Guid.Empty || expectedAggregateVersion < 0 || canonicalPayload.Length == 0)
        {
            throw new ArgumentException("A command id, nonnegative base version, and payload are required.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
            if (!IsSameRefreshedAuthority(access, authority))
            {
                throw new LocalCommandAccessDeniedException();
            }
            await using var db = await CreateReadyContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Commands.SingleOrDefaultAsync(
                item => item.CommandId == commandId.ToString("D"), cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The queued command does not exist.");
            EnsureOwnerAndEpoch(row, access);

            if (row.FrozenPayload is null)
            {
                if (row.State != LocalCommandState.Sending || row.LeaseUntilUtc <= _timeProvider.GetUtcNow())
                {
                    throw new InvalidOperationException("Only an unsent command with an active send lease may be frozen for delivery.");
                }

                var encryptedPayload = await EncryptPayloadAsync(
                    access, Guid.Parse(row.CommandId), Guid.Parse(row.CaseId), row.WorkspaceId, "sent",
                    canonicalPayload, cancellationToken).ConfigureAwait(false);
                row.FrozenExpectedAggregateVersion = expectedAggregateVersion;
                row.FrozenPayload = encryptedPayload.Ciphertext;
                row.FrozenPayloadNonce = encryptedPayload.Nonce;
                row.FrozenPayloadTag = encryptedPayload.Tag;
                row.FrozenPayloadHash = ComputePayloadHash(row, expectedAggregateVersion, canonicalPayload);
            }
            else if (row.FrozenExpectedAggregateVersion != expectedAggregateVersion ||
                     !row.FrozenPayload.AsSpan().SequenceEqual(canonicalPayload))
            {
                // Once a command has been attempted, neither its base version nor its bytes may change.
                // Keep the persisted first-send payload and hash. A retry must not replace them.
            }

            row.State = LocalCommandState.Sending;
            row.AttemptCount = checked(row.AttemptCount + 1);
            row.LeaseUntilUtc = _timeProvider.GetUtcNow().Add(SendingLease);
            row.NextAttemptAtUtc = null;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var frozenPayload = await DecryptPayloadAsync(row, row.FrozenPayload!, row.FrozenPayloadNonce,
                row.FrozenPayloadTag, "sent", cancellationToken).ConfigureAwait(false);
            return new OutboundCaseCommand(
                Guid.Parse(row.CommandId),
                Guid.Parse(row.CaseId),
                row.OwnerActorId,
                row.AccessEpoch,
                row.WorkspaceId,
                row.CommandType,
                row.FrozenExpectedAggregateVersion!.Value,
                frozenPayload,
                row.FrozenPayloadHash!.ToArray(),
                row.AttemptCount);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsSendContextCurrentAsync(
        OutboundCaseCommand command,
        SyncAccessResult authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(authority);
        var access = await _accessContextProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return IsSameRefreshedAuthority(access, authority) &&
            string.Equals(command.OwnerActorId, authority.ActorId, StringComparison.Ordinal) &&
            command.AccessEpoch == authority.AccessEpoch &&
            authority.PermittedWorkspaceIds.Contains(command.WorkspaceId) &&
            access!.PermittedWorkspaceIds.Contains(command.WorkspaceId, StringComparer.Ordinal);
    }

    public async Task MarkUnsentAuthorizationBlockedAsync(
        OutboundCaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await CreateReadyContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Commands.SingleOrDefaultAsync(
                item => item.CommandId == command.CommandId.ToString("D") &&
                    item.OwnerActorId == command.OwnerActorId && item.AccessEpoch == command.AccessEpoch,
                cancellationToken).ConfigureAwait(false);
            if (row is not null && row.State == LocalCommandState.Sending)
            {
                row.State = LocalCommandState.BlockedAuthorization;
                row.LeaseUntilUtc = null;
                row.NextAttemptAtUtc = null;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkAcknowledgedAsync(LocalCommandReceipt receipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.CommandId == Guid.Empty || receipt.CaseId == Guid.Empty || receipt.AggregateVersion <= 0)
        {
            throw new ArgumentException("A durable command receipt requires valid identifiers and aggregate version.", nameof(receipt));
        }

        await UpdateStateAsync(receipt.CommandId, cancellationToken, row =>
        {
            if (row.CaseId != receipt.CaseId.ToString("D") || row.FrozenPayload is null)
            {
                throw new LocalCommandReceiptException();
            }

            if (row.State is LocalCommandState.Acknowledged or LocalCommandState.Synchronized)
            {
                if (row.AcceptedAggregateVersion != receipt.AggregateVersion)
                {
                    throw new LocalCommandReceiptException();
                }

                return;
            }

            if (row.State != LocalCommandState.Sending)
            {
                throw new LocalCommandReceiptException();
            }

            row.State = LocalCommandState.Acknowledged;
            row.AcceptedAggregateVersion = receipt.AggregateVersion;
            row.LeaseUntilUtc = null;
            row.NextAttemptAtUtc = null;
        }).ConfigureAwait(false);
    }

    public async Task<bool> MarkSynchronizedAsync(
        Guid commandId,
        SyncAccessResult authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (commandId == Guid.Empty) throw new ArgumentException("A command identifier is required.", nameof(commandId));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
            if (!IsSameRefreshedAuthority(access, authority)) return false;

            await using var db = await CreateReadyContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Commands.SingleOrDefaultAsync(
                item => item.CommandId == commandId.ToString("D") && item.OwnerActorId == access.ActorId,
                cancellationToken).ConfigureAwait(false);
            if (row is null || row.State != LocalCommandState.Acknowledged ||
                !access.PermittedWorkspaceIds.Contains(row.WorkspaceId, StringComparer.Ordinal))
            {
                return false;
            }

            row.State = LocalCommandState.Synchronized;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkConflictAsync(
        Guid commandId,
        long latestAggregateVersion,
        byte[] latestAuthorizedSnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(latestAuthorizedSnapshot);
        if (latestAggregateVersion < 0 || latestAuthorizedSnapshot.Length == 0)
        {
            throw new ArgumentException("A current authorized snapshot and version are required.");
        }

        if (commandId == Guid.Empty) throw new ArgumentException("A command identifier is required.", nameof(commandId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
            await using var db = await CreateReadyContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Commands.SingleOrDefaultAsync(
                item => item.CommandId == commandId.ToString("D"), cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The queued command does not exist.");
            EnsureOwnerAndEpoch(row, access);
            if (row.State != LocalCommandState.Sending || row.FrozenPayload is null)
            {
                throw new InvalidOperationException("Only a sent command can become conflicted.");
            }

            var encryptedSnapshot = await EncryptPayloadAsync(
                access, commandId, Guid.Parse(row.CaseId), row.WorkspaceId, "conflict",
                latestAuthorizedSnapshot, cancellationToken).ConfigureAwait(false);
            row.State = LocalCommandState.BlockedConflict;
            row.LatestAuthorizedSnapshot = encryptedSnapshot.Ciphertext;
            row.LatestAuthorizedSnapshotNonce = encryptedSnapshot.Nonce;
            row.LatestAuthorizedSnapshotTag = encryptedSnapshot.Tag;
            row.LatestAggregateVersion = latestAggregateVersion;
            row.LeaseUntilUtc = null;
            row.NextAttemptAtUtc = null;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task MarkAuthorizationBlockedAsync(Guid commandId, CancellationToken cancellationToken) =>
        UpdateStateAsync(commandId, cancellationToken, row =>
        {
            row.State = LocalCommandState.BlockedAuthorization;
            row.LeaseUntilUtc = null;
            row.NextAttemptAtUtc = null;
        });

    public Task MarkValidationRejectedAsync(Guid commandId, CancellationToken cancellationToken) =>
        UpdateStateAsync(commandId, cancellationToken, row =>
        {
            row.State = LocalCommandState.RejectedValidation;
            row.LeaseUntilUtc = null;
            row.NextAttemptAtUtc = null;
        });

    public async Task ScheduleRetryAsync(Guid commandId, CancellationToken cancellationToken)
    {
        await UpdateStateAsync(commandId, cancellationToken, row =>
        {
            if (row.State != LocalCommandState.Sending)
            {
                throw new InvalidOperationException("Only a sending command can be scheduled for retry.");
            }

            var exponent = Math.Clamp(row.AttemptCount - 1, 0, 16);
            var baseSeconds = Math.Min(60d, Math.Pow(2, exponent));
            var jitter = Math.Clamp(_jitter(), 0d, 1d);
            var seconds = Math.Min(60d, baseSeconds * (0.75d + (0.5d * jitter)));
            row.State = LocalCommandState.RetryScheduled;
            row.NextAttemptAtUtc = _timeProvider.GetUtcNow().AddSeconds(seconds);
            row.LeaseUntilUtc = null;
        }).ConfigureAwait(false);
    }

    public Task RequestManualRetryAsync(Guid commandId, CancellationToken cancellationToken) =>
        UpdateStateAsync(commandId, cancellationToken, row =>
        {
            if (row.State != LocalCommandState.RetryScheduled)
            {
                throw new InvalidOperationException("Only a transiently failed command can be retried manually.");
            }

            row.NextAttemptAtUtc = _timeProvider.GetUtcNow();
        });

    public async Task<IReadOnlyList<LocalCommandStatus>> GetStatusesAsync(CancellationToken cancellationToken)
    {
        var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
        await using var db = await CreateReadyContextAsync(cancellationToken).ConfigureAwait(false);
        var permittedWorkspaceIds = access.PermittedWorkspaceIds.ToArray();
        var rows = await db.Commands.AsNoTracking()
            .Where(item => item.OwnerActorId == access.ActorId &&
                item.AccessEpoch == access.AccessEpoch &&
                permittedWorkspaceIds.Contains(item.WorkspaceId))
            .OrderBy(item => item.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => new LocalCommandStatus(
            Guid.Parse(row.CommandId),
            Guid.Parse(row.CaseId),
            row.State,
            row.BaseAggregateVersion,
            row.AcceptedAggregateVersion,
            row.DependsOnCommandId is null ? null : Guid.Parse(row.DependsOnCommandId),
            row.NextAttemptAtUtc)).ToArray();
    }

    public async Task<IReadOnlyList<Guid>> GetAcknowledgedCommandIdsForAuthorizedWorkspacesAsync(
        SyncAccessResult authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
        if (!IsSameRefreshedAuthority(access, authority)) return [];

        var permittedWorkspaceIds = access.PermittedWorkspaceIds.ToArray();
        if (permittedWorkspaceIds.Length == 0) return [];

        await using var db = await CreateReadyContextAsync(cancellationToken).ConfigureAwait(false);
        var commandIds = await db.Commands.AsNoTracking()
            .Where(item => item.OwnerActorId == access.ActorId &&
                item.State == LocalCommandState.Acknowledged &&
                permittedWorkspaceIds.Contains(item.WorkspaceId))
            .Select(item => item.CommandId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return commandIds.Select(Guid.Parse).ToArray();
    }

    public async Task<LocalCommandConflict?> GetConflictAsync(Guid commandId, CancellationToken cancellationToken)
    {
        var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
        await using var db = await CreateReadyContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Commands.AsNoTracking().SingleOrDefaultAsync(
            item => item.CommandId == commandId.ToString("D"), cancellationToken).ConfigureAwait(false);
        if (row is null || row.OwnerActorId != access.ActorId || row.AccessEpoch != access.AccessEpoch ||
            !access.PermittedWorkspaceIds.Contains(row.WorkspaceId, StringComparer.Ordinal) ||
            row.State != LocalCommandState.BlockedConflict || row.FrozenPayload is null ||
            row.LatestAuthorizedSnapshot is null || row.LatestAggregateVersion is null)
        {
            return null;
        }

        var frozenPayload = await DecryptPayloadAsync(row, row.FrozenPayload, row.FrozenPayloadNonce,
            row.FrozenPayloadTag, "sent", cancellationToken).ConfigureAwait(false);
        var authorizedSnapshot = await DecryptPayloadAsync(row, row.LatestAuthorizedSnapshot,
            row.LatestAuthorizedSnapshotNonce, row.LatestAuthorizedSnapshotTag, "conflict", cancellationToken).ConfigureAwait(false);

        return new LocalCommandConflict(
            Guid.Parse(row.CommandId), Guid.Parse(row.CaseId), row.WorkspaceId, row.AccessEpoch,
            row.FrozenExpectedAggregateVersion!.Value, frozenPayload, authorizedSnapshot, row.LatestAggregateVersion.Value);
    }

    private async Task UpdateStateAsync(Guid commandId, CancellationToken cancellationToken, Action<LocalCommandRow> update)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("A command identifier is required.", nameof(commandId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
            await using var db = await CreateReadyContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Commands.SingleOrDefaultAsync(
                item => item.CommandId == commandId.ToString("D"), cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The queued command does not exist.");
            if (!string.Equals(row.OwnerActorId, access.ActorId, StringComparison.Ordinal))
            {
                throw new LocalCommandAccessDeniedException();
            }
            update(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CommandOutboxDbContext> CreateReadyContextAsync(CancellationToken cancellationToken)
    {
        var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        return db;
    }

    private async ValueTask<LocalDraftAccessContext> RequireAccessAsync(
        string? workspaceId,
        CancellationToken cancellationToken)
    {
        var context = await _accessContextProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (context is null || !context.IsProvisioned || !context.IsActive ||
            string.IsNullOrWhiteSpace(context.ActorId) || context.AccessEpoch < 0 ||
            context.ExpiresAtUtc.Offset != TimeSpan.Zero || context.ExpiresAtUtc <= _timeProvider.GetUtcNow() ||
            context.PermittedWorkspaceIds is null ||
            (workspaceId is not null && !context.PermittedWorkspaceIds.Contains(workspaceId, StringComparer.Ordinal)))
        {
            throw new LocalCommandAccessDeniedException();
        }

        return context;
    }

    private async Task<EncryptedCommandPayload> EncryptPayloadAsync(
        LocalDraftAccessContext access,
        Guid commandId,
        Guid caseId,
        string workspaceId,
        string purpose,
        byte[] plaintext,
        CancellationToken cancellationToken)
    {
        var key = await _keyStore.GetOrCreateKeyAsync(access.ActorId, cancellationToken).ConfigureAwait(false);
        if (key is null || key.Length != 32)
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            throw new LocalCommandKeyUnavailableException();
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, CreateAssociatedData(
                access.ActorId, access.AccessEpoch, commandId, caseId, workspaceId, purpose));
            return new EncryptedCommandPayload(ciphertext, nonce, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<byte[]> DecryptPayloadAsync(
        LocalCommandRow row,
        byte[] ciphertext,
        byte[]? nonce,
        byte[]? tag,
        string purpose,
        CancellationToken cancellationToken)
    {
        if (row.EncryptionVersion != EncryptionVersion || nonce is null || tag is null ||
            nonce.Length != NonceSize || tag.Length != TagSize)
        {
            throw new LocalCommandIntegrityException();
        }

        var key = await _keyStore.GetOrCreateKeyAsync(row.OwnerActorId, cancellationToken).ConfigureAwait(false);
        if (key is null || key.Length != 32)
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            throw new LocalCommandKeyUnavailableException();
        }

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, CreateAssociatedData(
                row.OwnerActorId, row.AccessEpoch, Guid.Parse(row.CommandId), Guid.Parse(row.CaseId),
                row.WorkspaceId, purpose));
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new LocalCommandIntegrityException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] CreateAssociatedData(
        string actorId,
        long accessEpoch,
        Guid commandId,
        Guid caseId,
        string workspaceId,
        string purpose)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("SwiftAid.LocalCommand");
            writer.Write(EncryptionVersion);
            writer.Write(actorId);
            writer.Write(accessEpoch);
            writer.Write(commandId.ToByteArray());
            writer.Write(caseId.ToByteArray());
            writer.Write(workspaceId);
            writer.Write(purpose);
        }

        return stream.ToArray();
    }

    private static void EnsureOwnerAndEpoch(LocalCommandRow row, LocalDraftAccessContext access)
    {
        if (!string.Equals(row.OwnerActorId, access.ActorId, StringComparison.Ordinal) ||
            row.AccessEpoch != access.AccessEpoch ||
            !access.PermittedWorkspaceIds.Contains(row.WorkspaceId, StringComparer.Ordinal))
        {
            throw new LocalCommandAccessDeniedException();
        }
    }

    private bool IsSameRefreshedAuthority(
        LocalDraftAccessContext? access,
        SyncAccessResult authority)
    {
        if (!authority.IsReady || access is null || string.IsNullOrWhiteSpace(authority.ActorId) ||
            authority.PermittedWorkspaceIds is null || access.PermittedWorkspaceIds is null ||
            !access.IsProvisioned || !access.IsActive || access.AccessEpoch < 0 ||
            access.ExpiresAtUtc.Offset != TimeSpan.Zero || access.ExpiresAtUtc <= _timeProvider.GetUtcNow() ||
            access.AccessEpoch != authority.AccessEpoch ||
            !string.Equals(access.ActorId, authority.ActorId, StringComparison.Ordinal))
        {
            return false;
        }

        return access.PermittedWorkspaceIds.ToHashSet(StringComparer.Ordinal)
            .SetEquals(authority.PermittedWorkspaceIds);
    }

    private static bool CanSend(LocalCommandRow row, IReadOnlyList<LocalCommandRow> all, DateTimeOffset now)
    {
        var stateEligible = row.State switch
        {
            LocalCommandState.Queued => true,
            LocalCommandState.RetryScheduled => row.NextAttemptAtUtc is not DateTimeOffset retryAt || retryAt <= now,
            LocalCommandState.Sending => row.LeaseUntilUtc is not DateTimeOffset leaseUntil || leaseUntil <= now,
            _ => false
        };
        if (!stateEligible) return false;

        var sameCaseEarlier = all.Where(item => item.OwnerActorId == row.OwnerActorId &&
            item.AccessEpoch == row.AccessEpoch && item.CaseId == row.CaseId && item.Sequence < row.Sequence);
        if (sameCaseEarlier.Any(item => item.State is LocalCommandState.Queued or LocalCommandState.Sending or
                LocalCommandState.RetryScheduled or LocalCommandState.BlockedConflict or
                LocalCommandState.BlockedAuthorization or LocalCommandState.RejectedValidation or
                LocalCommandState.CancelledByUser))
        {
            return false;
        }

        if (row.DependsOnCommandId is null) return true;
        var dependency = all.SingleOrDefault(item => item.CommandId == row.DependsOnCommandId);
        return dependency?.State is LocalCommandState.Acknowledged or LocalCommandState.Synchronized;
    }

    private static long GetExpectedVersion(LocalCommandRow row, IReadOnlyList<LocalCommandRow> all)
    {
        if (row.FrozenExpectedAggregateVersion is long frozen) return frozen;
        if (row.DependsOnCommandId is null) return row.BaseAggregateVersion;
        var dependency = all.Single(item => item.CommandId == row.DependsOnCommandId);
        return dependency.AcceptedAggregateVersion ?? row.BaseAggregateVersion;
    }

    private async Task<LocalCommandProposal> ToProposalAsync(
        LocalCommandRow row,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var payload = await DecryptPayloadAsync(row, row.ProposalPayload, row.ProposalNonce,
            row.ProposalTag, "proposal", cancellationToken).ConfigureAwait(false);
        return new LocalCommandProposal(
            Guid.Parse(row.CommandId), Guid.Parse(row.CaseId), row.WorkspaceId, row.CommandType,
            expectedVersion, row.DependsOnCommandId is null ? null : Guid.Parse(row.DependsOnCommandId), payload);
    }

    private static byte[] ComputePayloadHash(LocalCommandRow row, long expectedVersion, byte[] payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("SwiftAid.LocalCommand.v1");
            writer.Write(row.CommandId);
            writer.Write(row.CaseId);
            writer.Write(row.OwnerActorId);
            writer.Write(row.AccessEpoch);
            writer.Write(row.WorkspaceId);
            writer.Write(row.CommandType);
            writer.Write(expectedVersion);
            writer.Write(payload.Length);
            writer.Write(payload);
        }

        return SHA256.HashData(stream.ToArray());
    }

    private static bool MatchesProposal(LocalCommandRow row, LocalCommandProposal proposal, byte[] decryptedPayload) =>
        row.CaseId == proposal.CaseId.ToString("D") &&
        string.Equals(row.WorkspaceId, proposal.WorkspaceId, StringComparison.Ordinal) &&
        string.Equals(row.CommandType, proposal.CommandType, StringComparison.Ordinal) &&
        row.BaseAggregateVersion == proposal.BaseAggregateVersion &&
        row.DependsOnCommandId == proposal.DependsOnCommandId?.ToString("D") &&
        decryptedPayload.AsSpan().SequenceEqual(proposal.ProposalPayload);

    private static void ValidateProposal(LocalCommandProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.CommandId == Guid.Empty || proposal.CaseId == Guid.Empty ||
            string.IsNullOrWhiteSpace(proposal.WorkspaceId) || proposal.WorkspaceId.Length > 200 ||
            string.IsNullOrWhiteSpace(proposal.CommandType) || proposal.CommandType.Length > 100 ||
            proposal.BaseAggregateVersion < 0 || proposal.ProposalPayload is null || proposal.ProposalPayload.Length == 0)
        {
            throw new ArgumentException("The local command proposal is invalid.", nameof(proposal));
        }

        if (proposal.DependsOnCommandId == proposal.CommandId)
        {
            throw new ArgumentException("A command cannot depend on itself.", nameof(proposal));
        }
    }
}

public interface ICommandOutbox
{
    Task EnqueueAsync(LocalCommandProposal proposal, CancellationToken cancellationToken);
    Task<bool> IsRefreshedAuthorityCurrentAsync(SyncAccessResult authority, CancellationToken cancellationToken);
    Task<LocalCommandProposal?> GetNextReadyAsync(SyncAccessResult authority, CancellationToken cancellationToken);
    Task<OutboundCaseCommand> FreezeForSendAsync(Guid commandId, long expectedAggregateVersion, byte[] canonicalPayload, SyncAccessResult authority, CancellationToken cancellationToken);
    Task<bool> IsSendContextCurrentAsync(OutboundCaseCommand command, SyncAccessResult authority, CancellationToken cancellationToken);
    Task MarkUnsentAuthorizationBlockedAsync(OutboundCaseCommand command, CancellationToken cancellationToken);
    Task MarkAcknowledgedAsync(LocalCommandReceipt receipt, CancellationToken cancellationToken);
    Task<bool> MarkSynchronizedAsync(Guid commandId, SyncAccessResult authority, CancellationToken cancellationToken);
    Task MarkConflictAsync(Guid commandId, long latestAggregateVersion, byte[] latestAuthorizedSnapshot, CancellationToken cancellationToken);
    Task MarkAuthorizationBlockedAsync(Guid commandId, CancellationToken cancellationToken);
    Task MarkValidationRejectedAsync(Guid commandId, CancellationToken cancellationToken);
    Task ScheduleRetryAsync(Guid commandId, CancellationToken cancellationToken);
    Task RequestManualRetryAsync(Guid commandId, CancellationToken cancellationToken);
    Task<IReadOnlyList<LocalCommandStatus>> GetStatusesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> GetAcknowledgedCommandIdsForAuthorizedWorkspacesAsync(SyncAccessResult authority, CancellationToken cancellationToken);
    Task<LocalCommandConflict?> GetConflictAsync(Guid commandId, CancellationToken cancellationToken);
}

public sealed class CommandOutboxDbContext(DbContextOptions<CommandOutboxDbContext> options) : DbContext(options)
{
    public DbSet<LocalCommandRow> Commands => Set<LocalCommandRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var command = modelBuilder.Entity<LocalCommandRow>();
        command.ToTable("staff_command_outbox");
        command.HasKey(item => item.CommandId);
        command.Property(item => item.CommandId).HasMaxLength(36).IsRequired();
        command.Property(item => item.CaseId).HasMaxLength(36).IsRequired();
        command.Property(item => item.WorkspaceId).HasMaxLength(200).IsRequired();
        command.Property(item => item.OwnerActorId).HasMaxLength(200).IsRequired();
        command.Property(item => item.AccessEpoch).IsRequired();
        command.Property(item => item.EncryptionVersion).IsRequired();
        command.Property(item => item.CommandType).HasMaxLength(100).IsRequired();
        command.Property(item => item.Sequence).IsRequired();
        command.Property(item => item.BaseAggregateVersion).IsRequired();
        command.Property(item => item.ProposalPayload).IsRequired();
        command.Property(item => item.ProposalNonce).IsRequired();
        command.Property(item => item.ProposalTag).IsRequired();
        command.Property(item => item.State).IsRequired();
        command.Property(item => item.CreatedAtUtc).IsRequired();
        command.HasIndex(item => new { item.OwnerActorId, item.AccessEpoch, item.CaseId, item.Sequence }).IsUnique();
        command.HasIndex(item => new { item.State, item.NextAttemptAtUtc, item.LeaseUntilUtc });
        command.HasIndex(item => new { item.OwnerActorId, item.AccessEpoch, item.WorkspaceId });
    }
}

public sealed class LocalCommandRow
{
    public string CommandId { get; set; } = string.Empty;
    public string CaseId { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public string OwnerActorId { get; set; } = string.Empty;
    public long AccessEpoch { get; set; }
    public int EncryptionVersion { get; set; }
    public string CommandType { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public long BaseAggregateVersion { get; set; }
    public string? DependsOnCommandId { get; set; }
    public byte[] ProposalPayload { get; set; } = [];
    public byte[] ProposalNonce { get; set; } = [];
    public byte[] ProposalTag { get; set; } = [];
    public byte[]? FrozenPayload { get; set; }
    public byte[]? FrozenPayloadNonce { get; set; }
    public byte[]? FrozenPayloadTag { get; set; }
    public byte[]? FrozenPayloadHash { get; set; }
    public long? FrozenExpectedAggregateVersion { get; set; }
    public LocalCommandState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public long? AcceptedAggregateVersion { get; set; }
    public byte[]? LatestAuthorizedSnapshot { get; set; }
    public byte[]? LatestAuthorizedSnapshotNonce { get; set; }
    public byte[]? LatestAuthorizedSnapshotTag { get; set; }
    public long? LatestAggregateVersion { get; set; }
}

internal sealed record EncryptedCommandPayload(byte[] Ciphertext, byte[] Nonce, byte[] Tag);

public sealed class CommandOutboxDbContextFactory : IDbContextFactory<CommandOutboxDbContext>
{
    private readonly string _connectionString;

    public CommandOutboxDbContextFactory(string appPrivateDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appPrivateDatabasePath);
        if (!Path.IsPathFullyQualified(appPrivateDatabasePath) || appPrivateDatabasePath == ":memory:")
        {
            throw new ArgumentException("A fully qualified app-private database file is required.", nameof(appPrivateDatabasePath));
        }

        _connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = appPrivateDatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
    }

    public CommandOutboxDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CommandOutboxDbContext>().UseSqlite(_connectionString).Options;
        return new CommandOutboxDbContext(options);
    }

    public Task<CommandOutboxDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }
}

public sealed class LocalCommandIdentityConflictException : Exception
{
    public LocalCommandIdentityConflictException() : base("The command identifier already belongs to a different immutable proposal.") { }
}

public sealed class LocalCommandDependencyException : Exception
{
    public LocalCommandDependencyException() : base("The command dependency is missing or belongs to another case/workspace.") { }
}

public sealed class LocalCommandReceiptException : Exception
{
    public LocalCommandReceiptException() : base("The server receipt does not match an in-flight local command.") { }
}

public sealed class LocalCommandAccessDeniedException : Exception
{
    public LocalCommandAccessDeniedException() : base("A current provisioned account and workspace grant are required for this local command.") { }
}

public sealed class LocalCommandKeyUnavailableException : Exception
{
    public LocalCommandKeyUnavailableException() : base("The protected local command key is unavailable.") { }
}

public sealed class LocalCommandIntegrityException : Exception
{
    public LocalCommandIntegrityException() : base("The saved local command failed integrity validation.") { }
}
