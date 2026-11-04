using System.Security.Cryptography;
using System.Text.Json;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Client.Intake;

/// <summary>Submits only the private guest intake queue; it never reads or synchronizes staff projections.</summary>
public sealed class GuestSubmissionCoordinator(
    IGuestSubmissionPersistence persistence,
    IGuestIntakeTransport transport,
    TimeProvider? timeProvider = null) : IGuestSubmissionCoordinator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<GuestSyncSummary> SubmitPendingAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = (await persistence.GetAllAsync(cancellationToken).ConfigureAwait(false))
                .Where(item => item.State == GuestSubmissionLocalState.Queued)
                .OrderBy(item => item.CreatedAtUtc)
                .ToArray();
            var received = 0;
            var retryPending = 0;
            var needsAttention = 0;

            foreach (var queuedItem in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = queuedItem;
                try
                {
                    if (!HasValidPayloadHash(item))
                    {
                        var updatedRecord = item with
                        {
                            State = GuestSubmissionLocalState.NeedsAttention,
                            IntakeCapability = null,
                            CapabilityExpiresAtUtc = null,
                            UpdatedAtUtc = _timeProvider.GetUtcNow(),
                            Version = checked(item.Version + 1)
                        };
                        await persistence.SaveVersionedAsync(updatedRecord, item.Version, cancellationToken).ConfigureAwait(false);
                        needsAttention++;
                        continue;
                    }

                    var capability = item.IntakeCapability;
                    var capabilityExpiry = item.CapabilityExpiresAtUtc;
                    if (item.SessionId == Guid.Empty)
                    {
                        // Upgrade pre-session drafts once, persisting the stable ID before I/O.
                        var versionBeforeSession = item.Version;
                        item = item with
                        {
                            SessionId = Guid.NewGuid(),
                            IntakeCapability = null,
                            CapabilityExpiresAtUtc = null,
                            UpdatedAtUtc = _timeProvider.GetUtcNow(),
                            Version = checked(item.Version + 1)
                        };
                        await persistence.SaveVersionedAsync(item, versionBeforeSession, cancellationToken).ConfigureAwait(false);
                        capability = null;
                        capabilityExpiry = null;
                    }

                    if (string.IsNullOrWhiteSpace(capability) || capabilityExpiry is null ||
                        capabilityExpiry.Value <= _timeProvider.GetUtcNow())
                    {
                        var session = await transport.CreateSessionAsync(
                            item.SessionId,
                            item.ReceiptProof,
                            cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(session.Capability) || session.ExpiresAtUtc <= _timeProvider.GetUtcNow())
                        {
                            retryPending++;
                            continue;
                        }

                        capability = session.Capability;
                        capabilityExpiry = session.ExpiresAtUtc;
                        var versionBeforeCapability = item.Version;
                        item = item with
                        {
                            IntakeCapability = capability,
                            CapabilityExpiresAtUtc = capabilityExpiry,
                            UpdatedAtUtc = _timeProvider.GetUtcNow(),
                            Version = checked(item.Version + 1)
                        };
                        await persistence.SaveVersionedAsync(item, versionBeforeCapability, cancellationToken).ConfigureAwait(false);
                    }

                    var receipt = await transport.SubmitAsync(
                        item.Submission,
                        item.SessionId,
                        capability,
                        item.ReceiptProof,
                        cancellationToken).ConfigureAwait(false);
                    if (receipt.ReceiptId == Guid.Empty ||
                        receipt.ClientSubmissionId != item.ClientSubmissionId ||
                        !Enum.IsDefined(receipt.Status) ||
                        !Enum.IsDefined(receipt.RoutingStatus) ||
                        receipt.ReceivedAtUtc.Offset != TimeSpan.Zero)
                    {
                        retryPending++;
                        continue;
                    }

                    var previousVersion = item.Version;
                    var acknowledged = item with
                    {
                        State = GuestSubmissionLocalState.Received,
                        Receipt = receipt,
                        IntakeCapability = null,
                        CapabilityExpiresAtUtc = null,
                        UpdatedAtUtc = _timeProvider.GetUtcNow(),
                        Version = checked(item.Version + 1)
                    };
                    await persistence.SaveVersionedAsync(acknowledged, previousVersion, cancellationToken).ConfigureAwait(false);
                    received++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (GuestIntakeTransportException exception) when (!exception.IsRetryable)
                {
                    var updatedRecord = item with
                    {
                        State = GuestSubmissionLocalState.NeedsAttention,
                        IntakeCapability = null,
                        CapabilityExpiresAtUtc = null,
                        UpdatedAtUtc = _timeProvider.GetUtcNow(),
                        Version = checked(item.Version + 1)
                    };
                    await persistence.SaveVersionedAsync(updatedRecord, item.Version, cancellationToken).ConfigureAwait(false);
                    needsAttention++;
                }
                catch (Exception)
                {
                    retryPending++;
                }
            }

            return new GuestSyncSummary(pending.Length, received, retryPending, needsAttention);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<GuestReceiptRefreshSummary> RefreshReceiptsAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var received = (await persistence.GetAllAsync(cancellationToken).ConfigureAwait(false))
                .Where(item => item.State == GuestSubmissionLocalState.Received && item.Receipt is not null)
                .OrderBy(item => item.CreatedAtUtc)
                .ToArray();
            var updated = 0;
            var retryPending = 0;

            foreach (var item in received)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentReceipt = item.Receipt!;
                try
                {
                    var receipt = await transport.GetReceiptAsync(
                        currentReceipt.ReceiptId,
                        item.ReceiptProof,
                        cancellationToken).ConfigureAwait(false);
                    if (receipt.ReceiptId != currentReceipt.ReceiptId ||
                        receipt.ClientSubmissionId != item.ClientSubmissionId ||
                        !Enum.IsDefined(receipt.Status) || !Enum.IsDefined(receipt.RoutingStatus) ||
                        receipt.ReceivedAtUtc.Offset != TimeSpan.Zero)
                    {
                        retryPending++;
                        continue;
                    }

                    if (receipt != currentReceipt)
                    {
                        var updatedRecord = item with
                        {
                            Receipt = receipt,
                            UpdatedAtUtc = _timeProvider.GetUtcNow(),
                            Version = checked(item.Version + 1)
                        };
                        await persistence.SaveVersionedAsync(updatedRecord, item.Version, cancellationToken).ConfigureAwait(false);
                        updated++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    retryPending++;
                }
            }

            return new GuestReceiptRefreshSummary(updated, retryPending);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool HasValidPayloadHash(GuestSubmissionRecord item)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(item.Submission, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        CryptographicOperations.ZeroMemory(payload);
        return string.Equals(hash, item.PayloadSha256, StringComparison.Ordinal);
    }
}

