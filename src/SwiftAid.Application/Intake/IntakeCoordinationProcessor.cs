using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Application.Intake;

/// <summary>PC-09 coordinator queue service: authorization first, then the store.</summary>
/// <remarks>
/// The service never decrypts sealed submissions and never accepts, links, or declines
/// reports. Organization acceptance stays on the reviewer path; the coordinator hands a
/// report to organization review by assigning an eligible configured queue.
/// </remarks>
public sealed class IntakeCoordinationProcessor(
    IIntakeCoordinationAuthorizer authorizer,
    IIntakeCoordinationStore store,
    TimeProvider timeProvider) : IIntakeCoordinationService
{
    private readonly IIntakeCoordinationAuthorizer _authorizer =
        authorizer ?? throw new ArgumentNullException(nameof(authorizer));
    private readonly IIntakeCoordinationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<IReadOnlyList<IntakeCoordinationQueueEntry>> GetQueueAsync(
        ActorContext actor,
        IntakeRoutingStatus? routingStatus,
        int limit,
        DateTimeOffset? receivedBeforeUtc,
        Guid? receiptBefore,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (routingStatus is not null && !Enum.IsDefined(routingStatus.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(routingStatus));
        }

        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var authority = await _authorizer.AuthorizeAsync(actor, ct).ConfigureAwait(false);
        if (authority is null)
        {
            return [];
        }

        return await _store.GetQueueAsync(routingStatus, limit, receivedBeforeUtc, receiptBefore, ct)
            .ConfigureAwait(false);
    }

    public async Task<IntakeCoordinationDetail?> GetDetailAsync(
        ActorContext actor,
        Guid receiptId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (receiptId == Guid.Empty)
        {
            return null;
        }

        var authority = await _authorizer.AuthorizeReportAsync(actor, receiptId, ct).ConfigureAwait(false);
        if (authority is null)
        {
            return null;
        }

        return await _store.GetDetailAsync(receiptId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IntakeCoordinationQueueOption>> GetEligibleQueuesAsync(
        ActorContext actor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var authority = await _authorizer.AuthorizeAsync(actor, ct).ConfigureAwait(false);
        if (authority is null)
        {
            return [];
        }

        return await _store.GetEligibleQueuesAsync(_timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
    }

    public async Task<IntakeDecisionResult> ActAsync(
        IntakeCoordinationCommand command,
        ActorContext actor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);
        if (command.ReceiptId == Guid.Empty || command.ExpectedVersion < 1 ||
            !Enum.IsDefined(command.Action) ||
            command.Reason is { Length: > 1000 } ||
            string.IsNullOrWhiteSpace(actor.ActorId))
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.Invalid, null);
        }

        var authority = await _authorizer.AuthorizeReportAsync(actor, command.ReceiptId, ct)
            .ConfigureAwait(false);
        if (authority is null)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.NotFoundOrNotAuthorized, null);
        }

        return await _store.ApplyAsync(command, authority, _timeProvider.GetUtcNow(), ct)
            .ConfigureAwait(false);
    }
}
