using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Ellai;

/// <summary>Checks current account identity for UI metadata only; grants no case access.</summary>
public interface IEllaiUiIdentityAuthority
{
    ValueTask<bool> IsCurrentAsync(ActorContext actor, CancellationToken ct);
}
