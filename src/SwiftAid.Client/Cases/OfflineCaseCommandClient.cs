using SwiftAid.Client.LocalStore;
using SwiftAid.Client.Outbox;
using SwiftAid.Client.Sync;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Cases;

/// <summary>
/// Offline-first staff case-command client. Every valid intent is queued durably in
/// the existing command outbox under one stable idempotency command id, then a sync
/// pass is attempted. Offline or failed sends keep the intent queued and report
/// <see cref="CaseCommandStatus.Unavailable"/> with that same id; terminal outcomes
/// map back as Accepted/Conflict/Denied/Invalid. No intent is ever dropped silently:
/// anything not terminal stays visible through <see cref="ICaseCommandOutboxTracker"/>.
/// </summary>
public sealed class OfflineCaseCommandClient(
    ICommandOutbox outbox,
    ISyncCoordinator syncCoordinator,
    ICaseTimelineClient timelineClient,
    ILocalDraftAccessContextProvider accessProvider) : ICaseCommandClient
{
    private const long MinimumWireExpectedVersion = 2;

    public async Task<CaseCommandResult> SendAsync(
        Guid caseId,
        CaseCommandInput input,
        CancellationToken cancellationToken = default)
    {
        var commandId = Guid.NewGuid();
        if (input is null || caseId == Guid.Empty || string.IsNullOrWhiteSpace(input.CommandType))
        {
            return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
        }

        var access = await accessProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!IsUsableAccess(access))
        {
            return new CaseCommandResult(CaseCommandStatus.Denied, commandId, [], null, null);
        }

        var workspaceId = await ResolveWorkspaceAsync(access!, caseId, cancellationToken).ConfigureAwait(false);
        if (workspaceId is null)
        {
            // Ambiguous case attribution with no reachable timeline: do not queue under a
            // guessed workspace. Surfaced with the caller-visible command id; nothing is stored.
            return new CaseCommandResult(CaseCommandStatus.Unavailable, commandId, [], null, null);
        }

        var baseVersion = await ResolveBaseVersionAsync(input, caseId, cancellationToken).ConfigureAwait(false);
        byte[] intentPayload;
        try
        {
            intentPayload = CaseCommandIntentCodec.Encode(CaseCommandIntentCodec.FromInput(input));
        }
        catch (ArgumentException)
        {
            return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
        }

        try
        {
            await outbox.EnqueueAsync(
                new LocalCommandProposal(
                    commandId, caseId, workspaceId, input.CommandType,
                    baseVersion, DependsOnCommandId: null, intentPayload),
                cancellationToken).ConfigureAwait(false);
        }
        catch (LocalCommandAccessDeniedException)
        {
            return new CaseCommandResult(CaseCommandStatus.Denied, commandId, [], null, null);
        }
        catch (Exception)
        {
            return new CaseCommandResult(CaseCommandStatus.Unavailable, commandId, [], null, null);
        }

        try
        {
            await syncCoordinator.SynchronizeAsync(SyncTrigger.Manual, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The intent stays queued; report it below from the retained outbox state.
        }

        return await MapRetainedOutcomeAsync(commandId, caseId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CaseCommandResult> MapRetainedOutcomeAsync(
        Guid commandId,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LocalCommandStatus> statuses;
        try
        {
            statuses = await outbox.GetStatusesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new CaseCommandResult(CaseCommandStatus.Unavailable, commandId, [], null, null);
        }

        var status = statuses.SingleOrDefault(item => item.CommandId == commandId);
        if (status is null)
        {
            return new CaseCommandResult(CaseCommandStatus.Unavailable, commandId, [], null, null);
        }

        return status.State switch
        {
            LocalCommandState.Acknowledged or LocalCommandState.Synchronized =>
                new CaseCommandResult(
                    CaseCommandStatus.Accepted, commandId, [],
                    status.AcceptedAggregateVersion, null),
            LocalCommandState.BlockedConflict =>
                await MapConflictAsync(commandId, cancellationToken).ConfigureAwait(false),
            LocalCommandState.BlockedAuthorization =>
                new CaseCommandResult(CaseCommandStatus.Denied, commandId, [], null, null),
            LocalCommandState.RejectedValidation =>
                new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null),
            _ => new CaseCommandResult(CaseCommandStatus.Unavailable, commandId, [], null, null),
        };
    }

    private async Task<CaseCommandResult> MapConflictAsync(
        Guid commandId,
        CancellationToken cancellationToken)
    {
        try
        {
            var conflict = await outbox.GetConflictAsync(commandId, cancellationToken).ConfigureAwait(false);
            return new CaseCommandResult(
                CaseCommandStatus.Conflict, commandId, [],
                conflict?.LatestAggregateVersion, null);
        }
        catch (Exception)
        {
            return new CaseCommandResult(CaseCommandStatus.Conflict, commandId, [], null, null);
        }
    }

    private async Task<string?> ResolveWorkspaceAsync(
        LocalDraftAccessContext access,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        var permitted = access.PermittedWorkspaceIds.Distinct(StringComparer.Ordinal).ToArray();
        if (permitted.Length == 1)
        {
            return permitted[0];
        }

        if (permitted.Length == 0)
        {
            return null;
        }

        var timeline = await TryGetTimelineAsync(caseId, cancellationToken).ConfigureAwait(false);
        return timeline?.Header is not null &&
            permitted.Contains(timeline.Header.WorkspaceId, StringComparer.Ordinal)
            ? timeline.Header.WorkspaceId
            : null;
    }

    private async Task<long> ResolveBaseVersionAsync(
        CaseCommandInput input,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        if (input.ExpectedVersion is >= MinimumWireExpectedVersion)
        {
            return input.ExpectedVersion.Value;
        }

        var timeline = await TryGetTimelineAsync(caseId, cancellationToken).ConfigureAwait(false);
        if (timeline?.Header is not null && timeline.Header.CaseId == caseId &&
            timeline.Header.AggregateVersion >= MinimumWireExpectedVersion)
        {
            return timeline.Header.AggregateVersion;
        }

        // Offline with no usable hint: send the minimum wire version. A stale version
        // surfaces as Conflict with a reconcile snapshot; the intent is never lost.
        return MinimumWireExpectedVersion;
    }

    private async Task<CaseTimeline?> TryGetTimelineAsync(Guid caseId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await timelineClient.GetAsync(caseId, null, cancellationToken).ConfigureAwait(false);
            return result.Access == CaseTimelineAccess.Found ? result.Timeline : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsUsableAccess(LocalDraftAccessContext? access) =>
        access is { IsProvisioned: true, IsActive: true } &&
        !string.IsNullOrWhiteSpace(access.ActorId) &&
        access.PermittedWorkspaceIds is not null;
}
