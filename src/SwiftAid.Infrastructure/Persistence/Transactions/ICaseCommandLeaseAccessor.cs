using SwiftAid.Application.Cases.Commands;

namespace SwiftAid.Infrastructure.Persistence.Transactions;

/// <summary>Exposes the active case lease to adapters sharing one scoped unit of work.</summary>
public interface ICaseCommandLeaseAccessor
{
    ICaseCommandLease? CurrentLease { get; }
}
