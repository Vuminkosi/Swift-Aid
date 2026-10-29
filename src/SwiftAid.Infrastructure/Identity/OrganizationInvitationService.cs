using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Identity;

public enum InvitationRedeemStatus
{
    Redeemed,
    AlreadyMember,
    AlreadyRequested,
    InvalidCode,
    Expired,
    Exhausted,
    OrganizationUnavailable,
    Unavailable
}

public sealed record InvitationRedeemResult(InvitationRedeemStatus Status, string? OrganizationId);

public interface IOrganizationInvitationService
{
    Task<InvitationRedeemResult> RedeemAsync(string actorId, string code, CancellationToken cancellationToken);
}

/// <summary>Member-side invitation redemption. A valid code fast-tracks a membership
/// request for staff review; it never creates membership or grants directly.
/// Issuance stays with staff tooling, which stores only the code hash.</summary>
public sealed class OrganizationInvitationService(
    SwiftAidDbContext dbContext,
    IOrganizationMembershipService membershipService,
    TimeProvider timeProvider) : IOrganizationInvitationService
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IOrganizationMembershipService _membershipService = membershipService ?? throw new ArgumentNullException(nameof(membershipService));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Deterministic code fingerprint shared with staff issuance tooling.</summary>
    public static string HashCode(string normalizedCode)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode));
        return Convert.ToHexString(bytes);
    }

    public static string? NormalizeCode(string? code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length < 4 || normalized.Length > 64)
        {
            return null;
        }

        foreach (var character in normalized)
        {
            var ok = (character >= 'A' && character <= 'Z') ||
                (character >= '0' && character <= '9') ||
                character == '-';
            if (!ok)
            {
                return null;
            }
        }

        return normalized;
    }

    public async Task<InvitationRedeemResult> RedeemAsync(
        string actorId, string code, CancellationToken cancellationToken)
    {
        var normalized = NormalizeCode(code);
        if (string.IsNullOrWhiteSpace(actorId) || normalized is null)
        {
            return new InvitationRedeemResult(InvitationRedeemStatus.InvalidCode, null);
        }

        try
        {
            var hash = HashCode(normalized);
            var invitation = await _dbContext.OrganizationInvitations
                .SingleOrDefaultAsync(entry => entry.CodeHash == hash, cancellationToken)
                .ConfigureAwait(false);
            if (invitation is null || string.Equals(invitation.Status, "Revoked", StringComparison.Ordinal))
            {
                return new InvitationRedeemResult(InvitationRedeemStatus.InvalidCode, null);
            }

            if (string.Equals(invitation.Status, "Exhausted", StringComparison.Ordinal))
            {
                return new InvitationRedeemResult(InvitationRedeemStatus.Exhausted, invitation.OrganizationId);
            }

            if (!string.Equals(invitation.Status, "Active", StringComparison.Ordinal))
            {
                return new InvitationRedeemResult(InvitationRedeemStatus.Expired, invitation.OrganizationId);
            }

            var now = _timeProvider.GetUtcNow();
            if (invitation.ExpiresAtUtc.HasValue && now >= invitation.ExpiresAtUtc.Value)
            {
                invitation.Status = "Expired";
                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new InvitationRedeemResult(InvitationRedeemStatus.Expired, invitation.OrganizationId);
            }

            if (invitation.UseCount >= invitation.MaxUses)
            {
                invitation.Status = "Exhausted";
                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new InvitationRedeemResult(InvitationRedeemStatus.Exhausted, invitation.OrganizationId);
            }

            var target = await _dbContext.Organizations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.OrganizationId == invitation.OrganizationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (target is null || target.Status != PersistentResourceStatus.Active)
            {
                return new InvitationRedeemResult(
                    InvitationRedeemStatus.OrganizationUnavailable, invitation.OrganizationId);
            }

            var request = await _membershipService.RequestAsync(
                    actorId, invitation.OrganizationId, "Invitation code redeemed", cancellationToken)
                .ConfigureAwait(false);
            switch (request.Status)
            {
                case MembershipRequestSubmitStatus.AlreadyMember:
                    return new InvitationRedeemResult(InvitationRedeemStatus.AlreadyMember, invitation.OrganizationId);
                case MembershipRequestSubmitStatus.OrganizationUnavailable:
                    return new InvitationRedeemResult(
                        InvitationRedeemStatus.OrganizationUnavailable, invitation.OrganizationId);
                case MembershipRequestSubmitStatus.Invalid:
                case MembershipRequestSubmitStatus.Unavailable:
                    return new InvitationRedeemResult(InvitationRedeemStatus.Unavailable, invitation.OrganizationId);
            }

            var pending = (await _dbContext.MembershipRequests
                .Where(entry => entry.ActorId == actorId &&
                    entry.OrganizationId == invitation.OrganizationId &&
                    entry.Status == MembershipRequestStatus.Pending)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
                .OrderByDescending(entry => entry.CreatedAtUtc)
                .FirstOrDefault();
            if (pending is not null)
            {
                pending.InvitationId = invitation.InvitationId;
                pending.UpdatedAtUtc = now;
            }

            invitation.UseCount++;
            if (invitation.UseCount >= invitation.MaxUses)
            {
                invitation.Status = "Exhausted";
            }

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return request.Status == MembershipRequestSubmitStatus.DuplicatePending
                ? new InvitationRedeemResult(InvitationRedeemStatus.AlreadyRequested, invitation.OrganizationId)
                : new InvitationRedeemResult(InvitationRedeemStatus.Redeemed, invitation.OrganizationId);
        }
        catch
        {
            return new InvitationRedeemResult(InvitationRedeemStatus.Unavailable, null);
        }
    }
}
