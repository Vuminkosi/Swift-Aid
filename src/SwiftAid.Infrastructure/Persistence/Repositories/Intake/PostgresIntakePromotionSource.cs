using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Intake;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

/// <summary>Reads sealed promotion sources and existing promotion links without locking.</summary>
/// <remarks>
/// The receiving organization resolves through the currently effective route for the
/// report's queue, mirroring <c>PostgresIntakeReviewAuthorizer</c>: promotion must stay
/// inside the same organization the reviewer was authorized for, even if routing rules
/// changed after the report arrived.
/// </remarks>
public sealed class PostgresIntakePromotionSource : IIntakePromotionSource
{
    private readonly SwiftAidDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public PostgresIntakePromotionSource(SwiftAidDbContext dbContext, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IntakePromotionSource?> ReadSourceAsync(Guid receiptId, CancellationToken ct)
    {
        if (receiptId == Guid.Empty)
        {
            return null;
        }

        var report = await _dbContext.IntakeReports.AsNoTracking()
            .Where(item => item.ReceiptId == receiptId)
            .Select(item => new
            {
                item.ReceiptId,
                item.ClientSubmissionId,
                item.Status,
                item.Version,
                item.RoutingStatus,
                item.IntakeQueueId,
                item.ProtectedPayload,
                item.ReceivedAtUtc
            })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (report is null || string.IsNullOrWhiteSpace(report.IntakeQueueId))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var route = await _dbContext.IntakeRoutes.AsNoTracking()
            .Where(item => item.IntakeQueueId == report.IntakeQueueId &&
                item.IsActive &&
                item.EffectiveFromUtc <= now &&
                (item.ExpiresAtUtc == null || item.ExpiresAtUtc > now))
            .OrderByDescending(item => item.EffectiveFromUtc)
            .Select(item => new { item.ReceivingOrganizationId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (route is null || string.IsNullOrWhiteSpace(route.ReceivingOrganizationId))
        {
            return null;
        }

        return new IntakePromotionSource(
            report.ReceiptId,
            report.ClientSubmissionId,
            report.Status,
            report.Version,
            report.RoutingStatus,
            report.IntakeQueueId,
            route.ReceivingOrganizationId,
            report.ProtectedPayload,
            report.ReceivedAtUtc);
    }

    public async Task<Guid?> FindPromotedCaseIdAsync(Guid receiptId, CancellationToken ct)
    {
        if (receiptId == Guid.Empty)
        {
            return null;
        }

        return await _dbContext.IntakePromotions.AsNoTracking()
            .Where(item => item.ReceiptId == receiptId)
            .Select(item => (Guid?)item.CaseId)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }
}
