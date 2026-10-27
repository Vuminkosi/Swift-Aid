using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Identity;

/// <summary>Creates a minimal first-party principal bound to the current actor access epoch.</summary>
public sealed class SwiftAidUserClaimsPrincipalFactory(
    UserManager<SwiftAidUser> userManager,
    IOptions<IdentityOptions> options,
    SwiftAidDbContext dbContext,
    TimeProvider timeProvider)
    : UserClaimsPrincipalFactory<SwiftAidUser>(userManager, options)
{
    public override async Task<ClaimsPrincipal> CreateAsync(SwiftAidUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var actor = await dbContext.Actors.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.ActorId == user.Id)
            .ConfigureAwait(false);
        var accessState = await dbContext.ActorAccessStates.AsNoTracking()
            .SingleOrDefaultAsync(state => state.ActorId == user.Id && state.AccessEpoch >= 0)
            .ConfigureAwait(false);
        if (actor is null || actor.Status != PersistentActorStatus.Active ||
            actor.ExpiresAtUtc is { } expiry && expiry <= timeProvider.GetUtcNow() ||
            accessState is null)
        {
            throw new InvalidOperationException("The SwiftAid identity is not currently active.");
        }

        var principal = await base.CreateAsync(user).ConfigureAwait(false);
        foreach (var identity in principal.Identities)
        {
            foreach (var claim in identity.FindAll(ClaimTypes.Name)
                         .Concat(identity.FindAll(ClaimTypes.Email))
                         .Concat(identity.FindAll(ClaimTypes.Role))
                         .ToArray())
            {
                identity.RemoveClaim(claim);
            }

            identity.AddClaim(new Claim(
                ActorContext.AccessEpochClaimType,
                accessState.AccessEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return principal;
    }
}
