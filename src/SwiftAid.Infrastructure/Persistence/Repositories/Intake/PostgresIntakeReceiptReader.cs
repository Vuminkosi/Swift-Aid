using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

/// <summary>Returns only the caller's minimal receipt after a session-scoped proof digest check.</summary>
public sealed class PostgresIntakeReceiptReader(SwiftAidDbContext dbContext) : IIntakeReceiptReader
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<IntakeReceipt?> GetAsync(
        Guid receiptId,
        ReadOnlyMemory<byte> receiptProofDigest,
        CancellationToken cancellationToken)
    {
        if (receiptId == Guid.Empty || receiptProofDigest.Length != 32)
        {
            return null;
        }

        var report = await _dbContext.Set<IntakeReportEntity>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.ReceiptId == receiptId, cancellationToken).ConfigureAwait(false);
        if (report is null)
        {
            return null;
        }

        var session = await _dbContext.Set<IntakeSessionEntity>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.SessionId == report.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || !CryptographicOperations.FixedTimeEquals(session.ReceiptProofDigest, receiptProofDigest.Span))
        {
            return null;
        }

        return new IntakeReceipt(report.ReceiptId, report.ClientSubmissionId, report.Status,
            report.RoutingStatus, report.ReceivedAtUtc);
    }
}
