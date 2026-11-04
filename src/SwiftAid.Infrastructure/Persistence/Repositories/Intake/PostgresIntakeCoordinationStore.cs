using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

/// <summary>
/// PC-09 coordination queue over unrouted reports. Reads project only the
/// minimum-necessary routing fields; the sealed submission is never decrypted here.
/// Writes run under the receipt lock with optimistic version concurrency and record
/// one audit row per action (actor, authority, reason, time, resulting state).
/// </summary>
public sealed class PostgresIntakeCoordinationStore(SwiftAidDbContext dbContext) : IIntakeCoordinationStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<IReadOnlyList<IntakeCoordinationQueueEntry>> GetQueueAsync(
        IntakeRoutingStatus? routingStatus,
        int limit,
        DateTimeOffset? receivedBeforeUtc,
        Guid? receiptBefore,
        CancellationToken ct)
    {
        if (routingStatus is not null && !Enum.IsDefined(routingStatus.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(routingStatus));
        }

        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var query = _dbContext.IntakeReports.AsNoTracking()
            .Where(item => item.IntakeQueueId == null &&
                (item.RoutingStatus == IntakeRoutingStatus.NeedsRouting ||
                    item.RoutingStatus == IntakeRoutingStatus.NoServiceCoverage) &&
                (item.Status == IntakeStatus.ReceivedUnverified ||
                    item.Status == IntakeStatus.AwaitingReview));
        if (routingStatus is not null)
        {
            query = query.Where(item => item.RoutingStatus == routingStatus.Value);
        }

        if (receivedBeforeUtc is not null && receiptBefore is { } beforeReceipt)
        {
            query = query.Where(item => item.ReceivedAtUtc < receivedBeforeUtc.Value ||
                (item.ReceivedAtUtc == receivedBeforeUtc.Value && item.ReceiptId.CompareTo(beforeReceipt) < 0));
        }

        return await query
            .OrderByDescending(item => item.ReceivedAtUtc)
            .ThenByDescending(item => item.ReceiptId)
            .Take(limit)
            .Select(item => new IntakeCoordinationQueueEntry(
                item.ReceiptId,
                item.Status,
                item.RoutingStatus,
                item.IntakeQueueId,
                item.RoutingCountryCode ?? string.Empty,
                item.RoutingRegionId,
                item.RoutingAreaId,
                item.RoutingCatalogueVersion,
                item.SubjectScope ?? IntakeSubjectScope.Human,
                item.ReceivedAtUtc,
                item.Version,
                item.CoordinatorActorId,
                item.ClarificationRequestedAtUtc != null))
            .ToArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<IntakeCoordinationDetail?> GetDetailAsync(Guid receiptId, CancellationToken ct)
    {
        if (receiptId == Guid.Empty)
        {
            return null;
        }

        var report = await _dbContext.IntakeReports.AsNoTracking()
            .Where(item => item.ReceiptId == receiptId)
            .Select(item => new IntakeCoordinationQueueEntry(
                item.ReceiptId,
                item.Status,
                item.RoutingStatus,
                item.IntakeQueueId,
                item.RoutingCountryCode ?? string.Empty,
                item.RoutingRegionId,
                item.RoutingAreaId,
                item.RoutingCatalogueVersion,
                item.SubjectScope ?? IntakeSubjectScope.Human,
                item.ReceivedAtUtc,
                item.Version,
                item.CoordinatorActorId,
                item.ClarificationRequestedAtUtc != null))
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (report is null)
        {
            return null;
        }

        var history = await _dbContext.IntakeCoordinationActions.AsNoTracking()
            .Where(item => item.ReceiptId == receiptId)
            .OrderBy(item => item.ActedAtUtc)
            .ThenBy(item => item.ActionId)
            .Select(item => new IntakeCoordinationActionRecord(
                item.ActionId,
                item.Action,
                item.ActorId,
                item.CoordinationOrganizationId,
                item.GrantGroupId,
                item.Reason,
                item.FromStatus,
                item.FromRouting,
                item.FromQueueId,
                item.ToStatus,
                item.ToRouting,
                item.ToQueueId,
                item.ResultingVersion,
                item.ActedAtUtc))
            .ToArrayAsync(ct).ConfigureAwait(false);

        return new IntakeCoordinationDetail(report, history);
    }

    public async Task<IntakeDecisionResult> ApplyAsync(
        IntakeCoordinationCommand command,
        IntakeCoordinationAuthority authority,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(authority);
        if (command.ReceiptId == Guid.Empty || command.ExpectedVersion < 1 ||
            !Enum.IsDefined(command.Action) ||
            command.Reason is { Length: > 1000 } || string.IsNullOrWhiteSpace(command.Reason) ||
            string.IsNullOrWhiteSpace(authority.ActorId) ||
            string.IsNullOrWhiteSpace(authority.CoordinationOrganizationId) ||
            string.IsNullOrWhiteSpace(authority.GrantGroupId))
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.Invalid, null);
        }

        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await AcquireReceiptLockAsync(command.ReceiptId, transaction.GetDbTransaction(), ct)
            .ConfigureAwait(false);

        var report = await _dbContext.Set<IntakeReportEntity>()
            .SingleOrDefaultAsync(item => item.ReceiptId == command.ReceiptId, ct).ConfigureAwait(false);
        if (report is null)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.NotFoundOrNotAuthorized, null);
        }

        var (status, effect) = IntakeCoordinationTransition.Evaluate(
            command, report.Status, report.Version, report.RoutingStatus,
            report.IntakeQueueId, report.CoordinatorActorId,
            report.ClarificationRequestedAtUtc is not null);
        if (status != IntakeDecisionStatus.Applied || effect is null)
        {
            return new IntakeDecisionResult(status, null);
        }

        if (effect is { AssignCoordinator: false, NextStatus: null, NextRouting: null } &&
            !effect.NextQueueIdSet && effect.SetClarification is null)
        {
            // No-op replay: record the audit reason against the unchanged state.
            _dbContext.Add(new IntakeCoordinationActionEntity
            {
                ActionId = Guid.NewGuid(),
                ReceiptId = report.ReceiptId,
                Action = command.Action,
                ActorId = authority.ActorId,
                CoordinationOrganizationId = authority.CoordinationOrganizationId,
                GrantGroupId = authority.GrantGroupId,
                Reason = command.Reason,
                FromStatus = report.Status,
                FromRouting = report.RoutingStatus,
                FromQueueId = report.IntakeQueueId,
                ToStatus = report.Status,
                ToRouting = report.RoutingStatus,
                ToQueueId = report.IntakeQueueId,
                ResultingVersion = report.Version,
                ActedAtUtc = nowUtc
            });
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new IntakeDecisionResult(IntakeDecisionStatus.Applied, ToReceipt(report));
        }

        if (effect.NextQueueIdSet)
        {
            var eligible = await IsQueueEligibleAsync(effect.NextQueueId!, nowUtc, ct).ConfigureAwait(false);
            if (!eligible)
            {
                return new IntakeDecisionResult(IntakeDecisionStatus.Invalid, null);
            }
        }

        var fromStatus = report.Status;
        var fromRouting = report.RoutingStatus;
        var fromQueue = report.IntakeQueueId;

        if (effect.AssignCoordinator)
        {
            report.CoordinatorActorId = authority.ActorId;
            report.CoordinatorAssignedAtUtc = nowUtc;
        }

        if (effect.NextStatus is { } nextStatus)
        {
            report.Status = nextStatus;
        }

        if (effect.NextRouting is { } nextRouting)
        {
            report.RoutingStatus = nextRouting;
        }

        if (effect.NextQueueIdSet)
        {
            report.IntakeQueueId = effect.NextQueueId;
        }

        if (effect.SetClarification is true)
        {
            report.ClarificationRequestedAtUtc = nowUtc;
            report.ClarificationReason = command.Reason;
        }

        report.Version += 1;
        report.DecidedAtUtc = nowUtc;
        report.DecisionActorId = authority.ActorId;
        report.ReviewReason = command.Reason;

        _dbContext.Add(new IntakeCoordinationActionEntity
        {
            ActionId = Guid.NewGuid(),
            ReceiptId = report.ReceiptId,
            Action = command.Action,
            ActorId = authority.ActorId,
            CoordinationOrganizationId = authority.CoordinationOrganizationId,
            GrantGroupId = authority.GrantGroupId,
            Reason = command.Reason,
            FromStatus = fromStatus,
            FromRouting = fromRouting,
            FromQueueId = fromQueue,
            ToStatus = report.Status,
            ToRouting = report.RoutingStatus,
            ToQueueId = report.IntakeQueueId,
            ResultingVersion = report.Version,
            ActedAtUtc = nowUtc
        });

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new IntakeDecisionResult(IntakeDecisionStatus.Applied, ToReceipt(report));
    }

    private async Task<bool> IsQueueEligibleAsync(string queueId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(queueId))
        {
            return false;
        }

        return await _dbContext.IntakeRoutes.AsNoTracking()
            .AnyAsync(item => item.IntakeQueueId == queueId &&
                item.IsActive &&
                item.EffectiveFromUtc <= nowUtc &&
                (item.ExpiresAtUtc == null || item.ExpiresAtUtc > nowUtc), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IntakeCoordinationQueueOption>> GetEligibleQueuesAsync(
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        return await _dbContext.IntakeRoutes.AsNoTracking()
            .Where(item => item.IsActive &&
                item.EffectiveFromUtc <= nowUtc &&
                (item.ExpiresAtUtc == null || item.ExpiresAtUtc > nowUtc))
            .OrderBy(item => item.IntakeQueueId)
            .Select(item => new IntakeCoordinationQueueOption(
                item.IntakeQueueId, item.ReceivingOrganizationId, item.CountryCode, item.CatalogueVersion))
            .Distinct()
            .ToArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task AcquireReceiptLockAsync(
        Guid receiptId,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@receipt_key, 0));";
        var parameter = new NpgsqlParameter("receipt_key", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = receiptId.ToString("N")
        };
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IntakeReceipt ToReceipt(IntakeReportEntity report) => new(
        report.ReceiptId,
        report.ClientSubmissionId,
        report.Status,
        report.RoutingStatus,
        report.ReceivedAtUtc);
}
