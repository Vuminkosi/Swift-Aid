using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Intake;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

/// <summary>Issues reusable session-scoped capabilities while retaining only cryptographic digests.</summary>
public sealed class PostgresIntakeSessionAuthority(
    SwiftAidDbContext dbContext,
    IntakeLimits intakeLimits,
    IOptions<IntakeSessionOptions> options) : IIntakeSessionAuthority
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IntakeLimits _intakeLimits = intakeLimits ?? throw new ArgumentNullException(nameof(intakeLimits));
    private readonly IntakeSessionOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<IntakeSessionResponse?> CreateOrReuseAsync(
        Guid sessionId,
        ReadOnlyMemory<byte> receiptProofDigest,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        _options.Validate();
        _intakeLimits.Validate();
        if (sessionId == Guid.Empty || receiptProofDigest.Length != 32)
        {
            return null;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireSessionLockAsync(sessionId, transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
        var session = await _dbContext.Set<IntakeSessionEntity>()
            .SingleOrDefaultAsync(item => item.SessionId == sessionId, cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            session = new IntakeSessionEntity
            {
                SessionId = sessionId,
                ReceiptProofDigest = receiptProofDigest.ToArray(),
                CreatedAtUtc = nowUtc,
                LastSeenAtUtc = nowUtc
            };
            _dbContext.Add(session);
        }
        else
        {
            if (!CryptographicOperations.FixedTimeEquals(session.ReceiptProofDigest, receiptProofDigest.Span))
            {
                return null;
            }

            session.LastSeenAtUtc = nowUtc;
        }

        var rawCapability = RandomNumberGenerator.GetBytes(32);
        var capabilityDigest = SHA256.HashData(rawCapability);
        var expiresAtUtc = nowUtc.AddMinutes(_options.CapabilityLifetimeMinutes);
        _dbContext.Add(new IntakeCapabilityEntity
        {
            CapabilityId = Guid.NewGuid(),
            SessionId = sessionId,
            CapabilityDigest = capabilityDigest,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = expiresAtUtc
        });

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new IntakeSessionResponse(WebEncoders.Base64UrlEncode(rawCapability), expiresAtUtc);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawCapability);
            CryptographicOperations.ZeroMemory(capabilityDigest);
        }
    }

    public async Task<IntakeCapability?> ValidateAsync(
        string opaqueCapability,
        Guid sessionId,
        ReadOnlyMemory<byte> receiptProofDigest,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        _options.Validate();
        _intakeLimits.Validate();
        if (string.IsNullOrWhiteSpace(opaqueCapability) || sessionId == Guid.Empty || receiptProofDigest.Length != 32)
        {
            return null;
        }

        byte[] raw;
        try
        {
            raw = WebEncoders.Base64UrlDecode(opaqueCapability);
        }
        catch (FormatException)
        {
            return null;
        }

        if (raw.Length != 32)
        {
            CryptographicOperations.ZeroMemory(raw);
            return null;
        }

        var digest = SHA256.HashData(raw);
        CryptographicOperations.ZeroMemory(raw);
        try
        {
            var session = await _dbContext.Set<IntakeSessionEntity>().AsNoTracking()
                .SingleOrDefaultAsync(item => item.SessionId == sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null || !CryptographicOperations.FixedTimeEquals(session.ReceiptProofDigest, receiptProofDigest.Span))
            {
                return null;
            }

            var capability = await _dbContext.Set<IntakeCapabilityEntity>().AsNoTracking()
                .SingleOrDefaultAsync(item => item.SessionId == sessionId && item.CapabilityDigest == digest &&
                    item.RevokedAtUtc == null && item.ExpiresAtUtc > nowUtc, cancellationToken).ConfigureAwait(false);
            return capability is null
                ? null
                : new IntakeCapability(
                    capability.CapabilityId,
                    sessionId,
                    session.ReceiptProofDigest,
                    capability.ExpiresAtUtc,
                    _intakeLimits.MaximumSubmissionsPerCapabilityPerHour);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
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
        command.Parameters.Add(new NpgsqlParameter("session_key", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = sessionId.ToString("N")
        });
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}
