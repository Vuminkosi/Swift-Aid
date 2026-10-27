using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Intake;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

/// <summary>Atomically accepts an idempotent guest report, its receipt projection, and its outbox event.</summary>
public sealed class PostgresIntakeSubmissionStore : IIntakeSubmissionStore
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SwiftAidDbContext _dbContext;
    private readonly IntakeLimits _intakeLimits;
    private readonly IDataProtector _payloadProtector;

    public PostgresIntakeSubmissionStore(
        SwiftAidDbContext dbContext,
        IntakeLimits intakeLimits,
        IDataProtectionProvider dataProtectionProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _intakeLimits = intakeLimits ?? throw new ArgumentNullException(nameof(intakeLimits));
        _payloadProtector = (dataProtectionProvider ?? throw new ArgumentNullException(nameof(dataProtectionProvider)))
            .CreateProtector("SwiftAid.Intake.Report.v1");
    }

    public async Task<IntakeReceipt> CommitAsync(
        IntakeSubmission submission,
        IntakeCapability capability,
        ReadOnlyMemory<byte> canonicalPayloadDigest,
        CoverageResult coverage,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(coverage);
        _intakeLimits.Validate();
        if (submission.ClientSubmissionId == Guid.Empty || capability.CapabilityId == Guid.Empty ||
            capability.SessionId == Guid.Empty || capability.ReceiptProofDigest.Length != 32 ||
            canonicalPayloadDigest.Length != 32 || capability.MaximumSubmissionsPerHour < 1 ||
            !Enum.IsDefined(coverage.Status))
        {
            throw new IntakeSubmissionException(IntakeErrorCode.CapabilityInvalid);
        }

        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await AcquireSessionLockAsync(capability.SessionId, transaction.GetDbTransaction(), cancellationToken)
            .ConfigureAwait(false);

        var session = await _dbContext.Set<IntakeSessionEntity>()
            .SingleOrDefaultAsync(item => item.SessionId == capability.SessionId, cancellationToken).ConfigureAwait(false);
        var activeCapability = await _dbContext.Set<IntakeCapabilityEntity>()
            .SingleOrDefaultAsync(item => item.CapabilityId == capability.CapabilityId &&
                item.SessionId == capability.SessionId && item.RevokedAtUtc == null &&
                item.ExpiresAtUtc > receivedAtUtc, cancellationToken).ConfigureAwait(false);
        if (session is null || activeCapability is null ||
            !CryptographicOperations.FixedTimeEquals(session.ReceiptProofDigest, capability.ReceiptProofDigest.Span))
        {
            throw new IntakeSubmissionException(IntakeErrorCode.CapabilityInvalid);
        }

        var existing = await _dbContext.Set<IntakeReportEntity>()
            .SingleOrDefaultAsync(item => item.SessionId == capability.SessionId &&
                item.ClientSubmissionId == submission.ClientSubmissionId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(existing.CanonicalPayloadDigest, canonicalPayloadDigest.Span))
            {
                throw new IntakeSubmissionException(IntakeErrorCode.SubmissionConflict);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToReceipt(existing);
        }

        var quota = Math.Min(capability.MaximumSubmissionsPerHour, _intakeLimits.MaximumSubmissionsPerCapabilityPerHour);
        var hourStart = receivedAtUtc.AddHours(-1);
        var successfulSubmissions = await _dbContext.Set<IntakeReportEntity>().AsNoTracking()
            .CountAsync(item => item.SessionId == capability.SessionId &&
                item.ReceivedAtUtc > hourStart && item.ReceivedAtUtc <= receivedAtUtc, cancellationToken)
            .ConfigureAwait(false);
        if (successfulSubmissions >= quota)
        {
            throw new IntakeSubmissionException(IntakeErrorCode.RateLimited);
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(submission, PayloadJsonOptions);
        byte[] protectedPayload;
        try
        {
            protectedPayload = _payloadProtector.Protect(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var receiptId = Guid.NewGuid();
        var status = coverage.Status == IntakeRoutingStatus.RoutedForReview
            ? IntakeStatus.AwaitingReview
            : IntakeStatus.ReceivedUnverified;
        var report = new IntakeReportEntity
        {
            ReceiptId = receiptId,
            SessionId = capability.SessionId,
            ClientSubmissionId = submission.ClientSubmissionId,
            CanonicalPayloadDigest = canonicalPayloadDigest.ToArray(),
            ProtectedPayload = protectedPayload,
            Status = status,
            RoutingStatus = coverage.Status,
            IntakeQueueId = coverage.Status == IntakeRoutingStatus.RoutedForReview ? coverage.IntakeQueueId : null,
            Version = 1,
            ReceivedAtUtc = receivedAtUtc
        };
        _dbContext.Add(report);
        _dbContext.Add(new IntakeOutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            ReceiptId = receiptId,
            Revision = 1,
            EventType = "IntakeReceived",
            CreatedAtUtc = receivedAtUtc,
            Attempts = 0
        });

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToReceipt(report);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedPayload);
        }
    }

    private async Task AcquireSessionLockAsync(
        Guid sessionId,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@session_key, 0));";
        var parameter = new NpgsqlParameter("session_key", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = sessionId.ToString("N")
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
