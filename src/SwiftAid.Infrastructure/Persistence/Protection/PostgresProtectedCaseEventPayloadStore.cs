using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence.Registration;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Infrastructure.Persistence.Protection;

/// <summary>Stages Data Protection ciphertext in the active case transaction.</summary>
/// <remarks>
/// Command journals stage through the command-lease guard; managed-case registration
/// stages through <see cref="StageProtectedForRegistrationAsync"/> under the active
/// create lease instead, because a not-yet-created case cannot satisfy the command
/// lease's case binding. Both paths share the same encryption and insert core.
/// </remarks>
public sealed class PostgresProtectedCaseEventPayloadStore : IProtectedCaseEventPayloadStore
{
    public const string CurrentPurposeVersion = "case-event-payload-v1";

    private readonly SwiftAidDbContext _dbContext;
    private readonly ICaseCommandLeaseAccessor _leaseAccessor;
    private readonly ProtectedPayloadOptions _options;
    private readonly ProtectedPayloadDataProtectionProvider? _dataProtectionProvider;
    private readonly TimeProvider _timeProvider;

    public PostgresProtectedCaseEventPayloadStore(
        SwiftAidDbContext dbContext,
        ICaseCommandLeaseAccessor leaseAccessor,
        ProtectedPayloadOptions options,
        ProtectedPayloadDataProtectionProvider? dataProtectionProvider,
        TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _leaseAccessor = leaseAccessor ?? throw new ArgumentNullException(nameof(leaseAccessor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _dataProtectionProvider = dataProtectionProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<ProtectedCaseEventPayloadReference> StageProtectedAsync(
        Guid caseId,
        Guid payloadId,
        string eventType,
        int schemaVersion,
        ReadOnlyMemory<byte> serializedBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureEnabled();
        ValidateMetadata(caseId, payloadId, eventType, schemaVersion);
        if (serializedBytes.Length > _options.MaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(serializedBytes), "The serialized payload exceeds its configured size limit.");
        }

        var lease = PostgresLeaseGuard.RequireCurrent(_dbContext, _leaseAccessor, caseId);
        if (lease.Mode != CaseLeaseMode.Write)
        {
            throw new InvalidOperationException("A write case lease is required to stage protected payloads.");
        }

        return StageCore(caseId, payloadId, eventType, schemaVersion, serializedBytes);
    }

    /// <summary>
    /// Stages ciphertext under an active registration create lease. The caller must already
    /// hold the lease; this verifies it is still the active create lease for this scoped
    /// context before sharing the command-side encryption and insert core.
    /// </summary>
    public ValueTask<ProtectedCaseEventPayloadReference> StageProtectedForRegistrationAsync(
        PostgresCaseRegistrationLease lease,
        Guid caseId,
        Guid payloadId,
        string eventType,
        int schemaVersion,
        ReadOnlyMemory<byte> serializedBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureEnabled();
        ValidateMetadata(caseId, payloadId, eventType, schemaVersion);
        if (serializedBytes.Length > _options.MaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(serializedBytes), "The serialized payload exceeds its configured size limit.");
        }

        if (!lease.IsActive || lease.Mode != CaseRegistrationLeaseMode.Create ||
            !ReferenceEquals(lease.DbContext, _dbContext) ||
            !ReferenceEquals(_dbContext.Database.CurrentTransaction, lease.Transaction))
        {
            throw new InvalidOperationException("An active registration create lease for this scoped PostgreSQL context is required.");
        }

        return StageCore(caseId, payloadId, eventType, schemaVersion, serializedBytes);
    }

    private ValueTask<ProtectedCaseEventPayloadReference> StageCore(
        Guid caseId,
        Guid payloadId,
        string eventType,
        int schemaVersion,
        ReadOnlyMemory<byte> serializedBytes)
    {
        var protector = CreatePurposeProtector(
            caseId,
            payloadId,
            eventType,
            schemaVersion,
            CurrentPurposeVersion);

        byte[] protectedBytes;
        var plaintextCopy = serializedBytes.ToArray();
        try
        {
            // Copy caller-owned memory before it enters the cryptographic operation.
            protectedBytes = protector.Protect(plaintextCopy);
        }
        catch (Exception exception) when (
            exception is CryptographicException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new ProtectedCaseEventPayloadException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextCopy);
        }

        if (protectedBytes.Length == 0)
        {
            throw new ProtectedCaseEventPayloadException();
        }

        _dbContext.ProtectedEventPayloads.Add(new ProtectedEventPayloadEntity
        {
            PayloadId = payloadId,
            ProtectedBytes = protectedBytes,
            ProtectionPurposeVersion = CurrentPurposeVersion,
            CreatedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime()
        });

        return ValueTask.FromResult(new ProtectedCaseEventPayloadReference(payloadId, CurrentPurposeVersion));
    }

    public async ValueTask<byte[]> UnprotectAsync(
        Guid caseId,
        Guid payloadId,
        string eventType,
        int schemaVersion,
        string protectionPurposeVersion,
        ReadOnlyMemory<byte> protectedBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureEnabled();
        ValidateMetadata(caseId, payloadId, eventType, schemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectionPurposeVersion);
        if (!string.Equals(protectionPurposeVersion, CurrentPurposeVersion, StringComparison.Ordinal))
        {
            throw new NotSupportedException("The protected payload purpose version is not supported.");
        }

        _ = PostgresLeaseGuard.RequireCurrent(_dbContext, _leaseAccessor, caseId);
        if (protectedBytes.IsEmpty)
        {
            throw new ProtectedCaseEventPayloadException();
        }

        var protectedBytesSnapshot = protectedBytes.ToArray();
        try
        {
            var persistedPayload = await _dbContext.ProtectedEventPayloads
                .AsNoTracking()
                .SingleOrDefaultAsync(entity => entity.PayloadId == payloadId, cancellationToken)
                .ConfigureAwait(false);
            if (persistedPayload is null ||
                !string.Equals(
                    persistedPayload.ProtectionPurposeVersion,
                    protectionPurposeVersion,
                    StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(persistedPayload.ProtectedBytes, protectedBytesSnapshot))
            {
                throw new ProtectedCaseEventPayloadException();
            }

            var hasMatchingEvent = await _dbContext.CaseEvents
                .AsNoTracking()
                .AnyAsync(entity =>
                    entity.CaseId == caseId &&
                    entity.PayloadId == payloadId &&
                    entity.EventType == eventType &&
                    entity.SchemaVersion == schemaVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!hasMatchingEvent)
            {
                throw new ProtectedCaseEventPayloadException();
            }

            var protector = CreatePurposeProtector(caseId, payloadId, eventType, schemaVersion, protectionPurposeVersion);
            var plaintext = protector.Unprotect(protectedBytesSnapshot);
            if (plaintext.LongLength > _options.MaximumPayloadBytes)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new ProtectedCaseEventPayloadException();
            }

            return plaintext;
        }
        catch (ProtectedCaseEventPayloadException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CryptographicException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new ProtectedCaseEventPayloadException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytesSnapshot);
        }
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled ||
            _options.MaximumPayloadBytes is <= 0 or > int.MaxValue)
        {
            throw new ProtectedCaseEventPayloadException();
        }
    }

    private IDataProtector CreatePurposeProtector(
        Guid caseId,
        Guid payloadId,
        string eventType,
        int schemaVersion,
        string purposeVersion)
    {
        var provider = _dataProtectionProvider ?? throw new ProtectedCaseEventPayloadException();
        return provider
            .CreateProtector("SwiftAid.ProtectedCaseEventPayload")
            .CreateProtector(purposeVersion)
            .CreateProtector($"case:{caseId:D}")
            .CreateProtector($"payload:{payloadId:D}")
            .CreateProtector($"event:{eventType}")
            .CreateProtector($"schema:{schemaVersion.ToString(CultureInfo.InvariantCulture)}");
    }

    private static void ValidateMetadata(Guid caseId, Guid payloadId, string eventType, int schemaVersion)
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("A case identifier is required.", nameof(caseId));
        }

        if (payloadId == Guid.Empty)
        {
            throw new ArgumentException("A payload identifier is required.", nameof(payloadId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        if (eventType.Length > 128 || !string.Equals(eventType, eventType.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical event type is required.", nameof(eventType));
        }

        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }
    }
}
