using SwiftAid.Client.Outbox;
using System.Security.Cryptography;

namespace SwiftAid.Client.Sync;

public enum SyncTrigger
{
    Resume,
    Manual,
    ConnectivityRestored
}

public enum CaseCommandDeliveryStatus
{
    Accepted,
    Conflict,
    AuthorizationDenied,
    ValidationRejected,
    TemporarilyUnavailable
}

public sealed record SyncSummary(
    int Sent,
    int AcceptedAwaitingRefresh,
    int Synchronized,
    int Conflicts,
    int AuthorizationBlocked,
    int ValidationRejected,
    int RetryScheduled,
    bool ProjectionRefreshSucceeded,
    bool AccessReady);

public sealed record SyncAccessResult(
    bool IsReady,
    string? ActorId,
    long AccessEpoch,
    IReadOnlySet<string> PermittedWorkspaceIds);

public sealed record CaseCommandDeliveryResult(
    CaseCommandDeliveryStatus Status,
    long? AcceptedAggregateVersion = null,
    long? LatestAggregateVersion = null,
    byte[]? LatestAuthorizedSnapshot = null);

public sealed record ProjectionRefreshResult(bool Succeeded, IReadOnlySet<Guid> ReflectedCommandIds);

/// <summary>Serializes one sync pass per service instance. Register as a device-scoped singleton.</summary>
public sealed class SyncCoordinator : ISyncCoordinator
{
    private readonly ICommandOutbox _outbox;
    private readonly ISyncAccessRefresher _accessRefresher;
    private readonly ICaseCommandPayloadBinder _payloadBinder;
    private readonly ICaseCommandTransport _commandTransport;
    private readonly IAuthorizedProjectionRefresher _projectionRefresher;
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public SyncCoordinator(
        ICommandOutbox outbox,
        ISyncAccessRefresher accessRefresher,
        ICaseCommandPayloadBinder payloadBinder,
        ICaseCommandTransport commandTransport,
        IAuthorizedProjectionRefresher projectionRefresher)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _accessRefresher = accessRefresher ?? throw new ArgumentNullException(nameof(accessRefresher));
        _payloadBinder = payloadBinder ?? throw new ArgumentNullException(nameof(payloadBinder));
        _commandTransport = commandTransport ?? throw new ArgumentNullException(nameof(commandTransport));
        _projectionRefresher = projectionRefresher ?? throw new ArgumentNullException(nameof(projectionRefresher));
    }

    public async Task<SyncSummary> SynchronizeAsync(SyncTrigger trigger, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(trigger)) throw new ArgumentOutOfRangeException(nameof(trigger));
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var access = await _accessRefresher.RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (!access.IsReady)
            {
                return CreateSummary(0, 0, 0, 0, 0, 0, 0, false, false);
            }

            if (!await _outbox.IsRefreshedAuthorityCurrentAsync(access, cancellationToken).ConfigureAwait(false))
            {
                return CreateSummary(0, 0, 0, 0, 0, 0, 0, false, false);
            }

            if (trigger == SyncTrigger.Manual)
            {
                var pendingRetries = await _outbox.GetStatusesAsync(cancellationToken).ConfigureAwait(false);
                foreach (var command in pendingRetries.Where(item => item.State == LocalCommandState.RetryScheduled))
                {
                    await _outbox.RequestManualRetryAsync(command.CommandId, cancellationToken).ConfigureAwait(false);
                }
            }

            var sent = 0;
            var conflicts = 0;
            var authorizationBlocked = 0;
            var validationRejected = 0;
            var retryScheduled = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var proposal = await _outbox.GetNextReadyAsync(access, cancellationToken).ConfigureAwait(false);
                if (proposal is null) break;

                byte[] canonicalPayload;
                try
                {
                    canonicalPayload = await _payloadBinder.BindAsync(
                        proposal, proposal.BaseAggregateVersion, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(proposal.ProposalPayload);
                }

                var command = await _outbox.FreezeForSendAsync(
                    proposal.CommandId, proposal.BaseAggregateVersion, canonicalPayload, access, cancellationToken).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(canonicalPayload);
                sent++;

                if (!await _outbox.IsSendContextCurrentAsync(command, access, cancellationToken).ConfigureAwait(false))
                {
                    CryptographicOperations.ZeroMemory(command.Payload);
                    await _outbox.MarkUnsentAuthorizationBlockedAsync(command, cancellationToken).ConfigureAwait(false);
                    authorizationBlocked++;
                    break;
                }

                CaseCommandDeliveryResult delivery;
                try
                {
                    delivery = await _commandTransport.SendAsync(command, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    await _outbox.ScheduleRetryAsync(command.CommandId, cancellationToken).ConfigureAwait(false);
                    retryScheduled++;
                    continue;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(command.Payload);
                }

                switch (delivery.Status)
                {
                    case CaseCommandDeliveryStatus.Accepted:
                        if (delivery.AcceptedAggregateVersion is not long aggregateVersion)
                        {
                            await _outbox.ScheduleRetryAsync(command.CommandId, cancellationToken).ConfigureAwait(false);
                            retryScheduled++;
                            break;
                        }

                        await _outbox.MarkAcknowledgedAsync(
                            new LocalCommandReceipt(command.CommandId, command.CaseId, aggregateVersion),
                            cancellationToken).ConfigureAwait(false);
                        break;

                    case CaseCommandDeliveryStatus.Conflict:
                        if (delivery.LatestAggregateVersion is not long latestVersion ||
                            delivery.LatestAuthorizedSnapshot is not { Length: > 0 } latestSnapshot)
                        {
                            await _outbox.ScheduleRetryAsync(command.CommandId, cancellationToken).ConfigureAwait(false);
                            retryScheduled++;
                            break;
                        }

                        await _outbox.MarkConflictAsync(
                            command.CommandId, latestVersion, latestSnapshot, cancellationToken).ConfigureAwait(false);
                        conflicts++;
                        break;

                    case CaseCommandDeliveryStatus.AuthorizationDenied:
                        await _outbox.MarkAuthorizationBlockedAsync(command.CommandId, cancellationToken).ConfigureAwait(false);
                        authorizationBlocked++;
                        break;

                    case CaseCommandDeliveryStatus.ValidationRejected:
                        await _outbox.MarkValidationRejectedAsync(command.CommandId, cancellationToken).ConfigureAwait(false);
                        validationRejected++;
                        break;

                    case CaseCommandDeliveryStatus.TemporarilyUnavailable:
                        await _outbox.ScheduleRetryAsync(command.CommandId, cancellationToken).ConfigureAwait(false);
                        retryScheduled++;
                        break;

                    default:
                        throw new InvalidOperationException("The command transport returned an unsupported status.");
                }
            }

            var refreshResult = await _projectionRefresher.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var acknowledged = await _outbox.GetAcknowledgedCommandIdsForAuthorizedWorkspacesAsync(
                access, cancellationToken).ConfigureAwait(false);
            var synchronized = 0;
            if (refreshResult.Succeeded)
            {
                foreach (var commandId in acknowledged.Where(refreshResult.ReflectedCommandIds.Contains))
                {
                    if (await _outbox.MarkSynchronizedAsync(commandId, access, cancellationToken).ConfigureAwait(false))
                    {
                        synchronized++;
                    }
                }
            }

            return CreateSummary(
                sent, acknowledged.Count - synchronized, synchronized, conflicts, authorizationBlocked,
                validationRejected, retryScheduled, refreshResult.Succeeded, true);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private static SyncSummary CreateSummary(
        int sent,
        int acceptedAwaitingRefresh,
        int synchronized,
        int conflicts,
        int authorizationBlocked,
        int validationRejected,
        int retryScheduled,
        bool refreshSucceeded,
        bool accessReady) =>
        new(sent, acceptedAwaitingRefresh, synchronized, conflicts, authorizationBlocked,
            validationRejected, retryScheduled, refreshSucceeded, accessReady);
}

public interface ISyncCoordinator
{
    Task<SyncSummary> SynchronizeAsync(SyncTrigger trigger, CancellationToken cancellationToken);
}

public interface ISyncAccessRefresher
{
    Task<SyncAccessResult> RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>Creates the exact first-send bytes using the server-acknowledged base version.</summary>
public interface ICaseCommandPayloadBinder
{
    Task<byte[]> BindAsync(LocalCommandProposal proposal, long expectedAggregateVersion, CancellationToken cancellationToken);
}

/// <summary>Adapter for the normal authenticated command API; implementation is owned by server integration work.</summary>
public interface ICaseCommandTransport
{
    Task<CaseCommandDeliveryResult> SendAsync(OutboundCaseCommand command, CancellationToken cancellationToken);
}

/// <summary>Refreshes only authorized flat projections and reports which acknowledged commands appeared.</summary>
public interface IAuthorizedProjectionRefresher
{
    Task<ProjectionRefreshResult> RefreshAsync(CancellationToken cancellationToken);
}
