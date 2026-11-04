using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Events;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Processing;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

internal sealed record LegacyEscalationJournalState(
    IReadOnlySet<Guid> AcknowledgedIds,
    IReadOnlySet<Guid> ActionRecordedIds);

/// <summary>Reads only legacy acknowledgement payloads needed to derive explicit escalation action state.</summary>
internal static class LegacyEscalationActionReader
{
    public static async Task<LegacyEscalationJournalState> ReadAsync(
        SwiftAidDbContext dbContext,
        IProtectedCaseEventPayloadStore payloadStore,
        ICaseCommandLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(payloadStore);
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.IsActive || lease.Mode != CaseLeaseMode.Read)
        {
            throw new UnauthorizedAccessException("An active case read lease is required to inspect escalation events.");
        }

        var rows = await (from journal in dbContext.CaseEvents.AsNoTracking()
                          join payload in dbContext.ProtectedEventPayloads.AsNoTracking()
                              on journal.PayloadId equals payload.PayloadId
                          where journal.CaseId == lease.CaseId &&
                                journal.EventType == nameof(CaseEventType.CaseEscalationAcknowledged)
                          orderby journal.AggregateVersion
                          select new { Journal = journal, Payload = payload })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        var acknowledgedIds = new HashSet<Guid>();
        var actionRecordedIds = new HashSet<Guid>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plaintext = await payloadStore.UnprotectAsync(
                row.Journal.CaseId,
                row.Journal.PayloadId,
                row.Journal.EventType,
                row.Journal.SchemaVersion,
                row.Payload.ProtectionPurposeVersion,
                row.Payload.ProtectedBytes,
                cancellationToken).ConfigureAwait(false);
            try
            {
                if (CaseEventJournalReader.Read(row.Journal, plaintext) is not SwiftAid.Domain.Cases.CaseEscalationAcknowledged acknowledgement ||
                    !acknowledgedIds.Add(acknowledgement.EscalationId))
                {
                    throw new InvalidDataException("The escalation journal contains an invalid acknowledgement sequence.");
                }

                if (!string.IsNullOrWhiteSpace(acknowledgement.ActionSummary))
                {
                    actionRecordedIds.Add(acknowledgement.EscalationId);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        return new LegacyEscalationJournalState(acknowledgedIds, actionRecordedIds);
    }
}
