namespace SwiftAid.Application.Authorization;

/// <summary>
/// Authoritative server-side identity state loaded directly from PostgreSQL for trusted background
/// authorization. It is not a client DTO and cannot be constructed outside the friend assembly.
/// </summary>
internal sealed record TrustedActorAuthorizationState(
    string ActorId,
    long AccessEpoch,
    bool IsActive,
    DateTimeOffset? ExpiresAtUtc);
