using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Application.Sessions;

/// <summary>Current source approval, rights, applicability and withdrawal decision for session material versions.</summary>
/// <remarks>
/// No production implementation is currently registered. A future authority must be integrated
/// with the session write transaction so withdrawal cannot race publication; this interface
/// alone does not establish that guarantee.
/// </remarks>
public interface ISessionMaterialEligibility
{
    Task<bool> AreEligibleAsync(SessionMaterialEligibilityRequest request, CancellationToken cancellationToken);
}

public sealed record SessionMaterialEligibilityRequest(
    string CountryCode,
    string RegionId,
    string Sector,
    string Language,
    SessionVisibility Visibility,
    IReadOnlyList<SessionMaterialRef> Materials);
