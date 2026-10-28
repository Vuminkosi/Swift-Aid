using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>
/// Lists the calling officer's owned managed cases newest-first by the most
/// recently recorded journal event. Cases without journal rows fall back to the
/// projection timestamp. No sealed payloads are read.
/// </summary>
public sealed class PostgresCaseQueueStore(SwiftAidDbContext dbContext) : ICaseQueueStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<IReadOnlyList<CaseQueueCandidate>> GetOwnedCasesAsync(
        string ownerActorId, int take, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerActorId);
        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "The queue take must be positive.");
        }

        var rows = await _dbContext.ManagedCases.AsNoTracking()
            .Where(item => item.AccountableOwnerActorId == ownerActorId)
            .Select(item => new
            {
                item.CaseId,
                Workflow = item.Workflow.ToString(),
                item.CountryCode,
                item.AccountableOwnerActorId,
                item.AggregateVersion,
                item.UpdatedAtUtc,
                LastRecordedAtUtc = _dbContext.CaseEvents
                    .Where(journal => journal.CaseId == item.CaseId)
                    .Max(journal => (DateTimeOffset?)journal.RecordedAtUtc)
            })
            .OrderByDescending(item => item.LastRecordedAtUtc ?? item.UpdatedAtUtc)
            .ThenBy(item => item.CaseId)
            .Take(take)
            .ToArrayAsync(ct).ConfigureAwait(false);

        return rows
            .Select(item => new CaseQueueCandidate(
                item.CaseId,
                item.Workflow,
                item.CountryCode,
                item.AccountableOwnerActorId,
                item.AggregateVersion,
                item.UpdatedAtUtc,
                item.LastRecordedAtUtc ?? item.UpdatedAtUtc))
            .ToArray();
    }
}
