using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

public sealed class PostgresCaseWorkDiscoveryStore(SwiftAidDbContext dbContext) : ICaseWorkDiscoveryStore
{
    public async Task<IReadOnlyList<Guid>> GetCasesAfterAsync(CaseWorkDiscoveryKind kind, string? assignedActorId,
        Guid? after, int take, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(kind) || after == Guid.Empty || take is < 1 or > 200 ||
            assignedActorId is not null && string.IsNullOrWhiteSpace(assignedActorId) ||
            kind == CaseWorkDiscoveryKind.Escalation && assignedActorId is not null)
            throw new ArgumentException("The case work discovery request is invalid.");

        IQueryable<Guid> cases = kind switch
        {
            CaseWorkDiscoveryKind.FollowUp => dbContext.ManagedCaseFollowUps.AsNoTracking()
                .Where(item => (item.Status == FollowUpStatus.Scheduled || item.Status == FollowUpStatus.Acknowledged) &&
                    (assignedActorId == null || item.AssigneeActorId == assignedActorId)).Select(item => item.CaseId),
            CaseWorkDiscoveryKind.Escalation => dbContext.ManagedCaseEscalations.AsNoTracking()
                .Where(item => item.Status != EscalationStatus.Closed).Select(item => item.CaseId),
            CaseWorkDiscoveryKind.Assistance => dbContext.ManagedCaseAssistance.AsNoTracking()
                .Where(item => item.Status != AssistanceStatus.ReceiptConfirmed && item.Status != AssistanceStatus.Cancelled &&
                    (assignedActorId == null || item.ResponsibleActorId == assignedActorId)).Select(item => item.CaseId),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (after is { } position) cases = cases.Where(caseId => caseId.CompareTo(position) > 0);
        return await cases.Distinct().OrderBy(caseId => caseId).Take(take).ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }
}
