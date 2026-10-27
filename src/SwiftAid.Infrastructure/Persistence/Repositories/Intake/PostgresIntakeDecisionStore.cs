using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

/// <summary>
/// Persists reviewer triage decisions (route for review, link as evidence, decline) with
/// optimistic version concurrency and a transaction-scoped receipt lock, so two officers
/// deciding the same receipt cannot both apply: the loser observes the version change and
/// receives <c>Conflict</c>. An identical replay of an already-recorded decision returns
/// <c>Applied</c> with the current receipt without rewriting state.
/// </summary>
/// <remarks>
/// <c>AcceptIntoCase</c> is evaluated by <see cref="IntakeDecisionTransition"/> and throws
/// <see cref="IntakePromotionUnavailableException"/>: the unique promotion link must be
/// atomic with the first managed-case events (spec §6), and the managed-case registration
/// execution backend has no production implementation yet. Actor authorization (catchment
/// reviewer) is enforced by the staff endpoint; the store records the deciding actor and
/// enforces data-integrity rules only.
/// </remarks>
public sealed class PostgresIntakeDecisionStore : IIntakeDecisionStore
{
    private readonly SwiftAidDbContext _dbContext;

    public PostgresIntakeDecisionStore(SwiftAidDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IntakeDecisionResult> DecideAsync(
        IntakeDecision decision,
        ActorContext actor,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(actor);
        if (decision.ReceiptId == Guid.Empty || decision.ExpectedVersion < 1 ||
            !Enum.IsDefined(decision.Decision) || decision.ReviewReason is { Length: > 1000 } ||
            string.IsNullOrWhiteSpace(actor.ActorId))
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.Invalid, null);
        }

        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await AcquireReceiptLockAsync(decision.ReceiptId, transaction.GetDbTransaction(), ct)
            .ConfigureAwait(false);

        var report = await _dbContext.Set<IntakeReportEntity>()
            .SingleOrDefaultAsync(item => item.ReceiptId == decision.ReceiptId, ct).ConfigureAwait(false);
        if (report is null)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.NotFoundOrNotAuthorized, null);
        }

        // Evidence links are write-once: naming a different target after a link was recorded
        // conflicts instead of silently affirming the original link.
        if (decision.Decision == IntakeDecisionKind.LinkAsEvidence &&
            decision.TargetCaseId is { } requestedTarget && requestedTarget != Guid.Empty)
        {
            var linkedCaseId = await _dbContext.Set<IntakeEvidenceLinkEntity>()
                .Where(item => item.ReceiptId == decision.ReceiptId)
                .Select(item => (Guid?)item.CaseId)
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (linkedCaseId is not null && linkedCaseId != requestedTarget)
            {
                return new IntakeDecisionResult(IntakeDecisionStatus.Conflict, null);
            }
        }

        var (status, next) = IntakeDecisionTransition.Evaluate(
            decision, report.Status, report.Version, report.RoutingStatus, report.IntakeQueueId);
        if (status != IntakeDecisionStatus.Applied || next is null)
        {
            return new IntakeDecisionResult(status, null);
        }

        if (next == report.Status)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.Applied, ToReceipt(report));
        }

        if (decision.Decision == IntakeDecisionKind.LinkAsEvidence)
        {
            var linkResult = await ApplyEvidenceLinkAsync(decision, report, actor, nowUtc, ct).ConfigureAwait(false);
            if (linkResult is not null)
            {
                return linkResult;
            }
        }

        report.Status = next.Value;
        report.Version += 1;
        report.DecidedAtUtc = nowUtc;
        report.DecisionActorId = actor.ActorId;
        report.ReviewReason = decision.ReviewReason;

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new IntakeDecisionResult(IntakeDecisionStatus.Applied, ToReceipt(report));
    }

    /// <summary>
    /// Persists the evidence link atomically with the decision. A replay naming the same case
    /// returns <c>null</c> so the caller proceeds with the status transition; a missing case
    /// (removed after authorization) or a different target returns a terminal result.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the link was recorded and the caller should apply the report update;
    /// otherwise the terminal decision result.
    /// </returns>
    private async Task<IntakeDecisionResult?> ApplyEvidenceLinkAsync(
        IntakeDecision decision,
        IntakeReportEntity report,
        ActorContext actor,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        if (decision.TargetCaseId is not Guid linkTarget || linkTarget == Guid.Empty)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.Invalid, null);
        }

        var existing = await _dbContext.Set<IntakeEvidenceLinkEntity>()
            .SingleOrDefaultAsync(item => item.ReceiptId == decision.ReceiptId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.CaseId == decision.TargetCaseId.Value
                ? new IntakeDecisionResult(IntakeDecisionStatus.Applied, ToReceipt(report))
                : new IntakeDecisionResult(IntakeDecisionStatus.Conflict, null);
        }

        var caseExists = await _dbContext.ManagedCases.AsNoTracking()
            .AnyAsync(item => item.CaseId == decision.TargetCaseId.Value, ct).ConfigureAwait(false);
        if (!caseExists)
        {
            return new IntakeDecisionResult(IntakeDecisionStatus.Conflict, null);
        }

        _dbContext.Add(new IntakeEvidenceLinkEntity
        {
            ReceiptId = decision.ReceiptId,
            CaseId = decision.TargetCaseId.Value,
            LinkingOfficerActorId = actor.ActorId,
            SourceRevision = report.Version,
            LinkedAtUtc = nowUtc
        });
        return null;
    }

    private async Task AcquireReceiptLockAsync(
        Guid receiptId,
        System.Data.Common.DbTransaction transaction,
        CancellationToken ct)
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
        await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static IntakeReceipt ToReceipt(IntakeReportEntity report) => new(
        report.ReceiptId,
        report.ClientSubmissionId,
        report.Status,
        report.RoutingStatus,
        report.ReceivedAtUtc);
}
