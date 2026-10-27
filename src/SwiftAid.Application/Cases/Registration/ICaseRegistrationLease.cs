using System.Collections.Immutable;

namespace SwiftAid.Application.Cases.Registration;

public enum CaseRegistrationLeaseMode
{
    Create,
    ExistingCaseReplay
}

/// <summary>A registration transaction lease. Replay leases are bound to one existing case; create leases have no case id.</summary>
public interface ICaseRegistrationLease : IAsyncDisposable
{
    Guid? CaseId { get; }

    CaseRegistrationLeaseMode Mode { get; }

    ImmutableArray<string> LockedActorIds { get; }

    bool IsActive { get; }

    ValueTask CommitAsync(CancellationToken cancellationToken);
}
