using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Application.Sessions;

/// <summary>Current source approval, rights, applicability and withdrawal decision for session material versions.</summary>
/// <remarks>
/// The production implementation checks organization-owner approval for an exact material version.
/// </remarks>
public interface ISessionMaterialEligibility
{
    Task<bool> AreEligibleAsync(SessionMaterialEligibilityRequest request, CancellationToken cancellationToken);
}

public interface ISessionMaterialApprovalManager
{
    Task<bool> RevokeAsync(string actorId, string organizationId, string materialId, string sourceVersion, CancellationToken cancellationToken);
}

public sealed record SessionMaterialEligibilityRequest(
    string OrganizationId,
    string CountryCode,
    string RegionId,
    string Sector,
    string Language,
    SessionVisibility Visibility,
    IReadOnlyList<SessionMaterialRef> Materials);
