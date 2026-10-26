using System.Collections.Immutable;

namespace SwiftAid.Application.Cases.Commands;

/// <summary>A case-bound transaction lease held until commit or disposal.</summary>
public interface ICaseCommandLease : IAsyncDisposable
{
    Guid CaseId { get; }

    CaseLeaseMode Mode { get; }

    ImmutableArray<string> LockedActorIds { get; }

    bool IsActive { get; }

    ValueTask CommitAsync(CancellationToken cancellationToken);
}
