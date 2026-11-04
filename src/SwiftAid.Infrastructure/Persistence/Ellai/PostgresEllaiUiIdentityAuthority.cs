using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

/// <summary>Navigation is account metadata and does not require a case transaction.</summary>
public sealed class PostgresEllaiUiIdentityAuthority(SwiftAidDbContext db, TimeProvider? clock = null)
    : IEllaiUiIdentityAuthority
{
    public async ValueTask<bool> IsCurrentAsync(ActorContext actor, CancellationToken ct)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        return await (from account in db.Actors.AsNoTracking()
                      join access in db.ActorAccessStates.AsNoTracking() on account.ActorId equals access.ActorId
                      where account.ActorId == actor.ActorId && access.AccessEpoch == actor.AccessEpoch &&
                          account.Status == PersistentActorStatus.Active &&
                          (account.ExpiresAtUtc == null || account.ExpiresAtUtc > now)
                      select account.ActorId).AnyAsync(ct).ConfigureAwait(false);
    }
}
