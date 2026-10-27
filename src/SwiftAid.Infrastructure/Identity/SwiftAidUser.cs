using Microsoft.AspNetCore.Identity;

namespace SwiftAid.Infrastructure.Identity;

/// <summary>
/// Authentication-only user record. The key is the opaque internal actor ID;
/// organization membership and case permissions remain in the authority tables.
/// </summary>
public sealed class SwiftAidUser : IdentityUser
{
}

/// <summary>Maps a validated external provider subject to one internal actor.</summary>
public sealed class ExternalIdentityMappingEntity
{
    public Guid MappingId { get; set; }

    public required string ActorId { get; set; }

    public required string Provider { get; set; }

    public required string Issuer { get; set; }

    public required string Subject { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
