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

public enum InvitationIssueStatus
{
    Issued,
    NotStaff,
    OrganizationUnavailable,
    Invalid,
    Unavailable
}

public sealed record InvitationIssueResult(
    InvitationIssueStatus Status,
    string? Code,
    Guid? InvitationId,
    DateTimeOffset? ExpiresAtUtc);

public interface IOrganizationInvitationService
{
    Task<InvitationRedeemResult> RedeemAsync(string actorId, string code, CancellationToken cancellationToken);

    Task<InvitationIssueResult> CreateAsync(
        string actorId,
        string organizationId,
        int maxUses,
        DateTimeOffset? expiresAtUtc,
        CancellationToken cancellationToken);
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

    public async Task<InvitationIssueResult> CreateAsync(
        string actorId,
        string organizationId,
        int maxUses,
        DateTimeOffset? expiresAtUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(organizationId))
        {
            return new InvitationIssueResult(InvitationIssueStatus.Invalid, null, null, null);
        }

        if (maxUses < 1 || maxUses > 100)
        {
            return new InvitationIssueResult(InvitationIssueStatus.Invalid, null, null, null);
        }

        var now = _timeProvider.GetUtcNow();
        if (expiresAtUtc.HasValue && expiresAtUtc.Value <= now)
        {
            return new InvitationIssueResult(InvitationIssueStatus.Invalid, null, null, null);
        }

        try
        {
            var target = await _dbContext.Organizations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (target is null || target.Status != PersistentResourceStatus.Active)
            {
                return new InvitationIssueResult(InvitationIssueStatus.OrganizationUnavailable, null, null, null);
            }

            var memberships = await _membershipService.GetMineAsync(actorId, cancellationToken).ConfigureAwait(false);
            var staff = memberships.Any(entry =>
                string.Equals(entry.OrganizationId, organizationId, StringComparison.Ordinal) &&
                string.Equals(
                    entry.MembershipStatus, nameof(PersistentMembershipStatus.Active), StringComparison.Ordinal) &&
                entry.WorkspaceIds.Count > 0);
            if (!staff)
            {
                return new InvitationIssueResult(InvitationIssueStatus.NotStaff, null, null, null);
            }

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var code = GenerateCode();
                var hash = HashCode(code);
                if (await _dbContext.OrganizationInvitations
                    .AnyAsync(entry => entry.CodeHash == hash, cancellationToken)
                    .ConfigureAwait(false))
                {
                    continue;
                }

                var invitation = new OrganizationInvitationEntity
                {
                    InvitationId = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    CodeHash = hash,
                    Status = "Active",
                    ExpiresAtUtc = expiresAtUtc,
                    MaxUses = maxUses,
                    UseCount = 0,
                    CreatedByActorId = actorId,
                    CreatedAtUtc = now,
                };
                _dbContext.OrganizationInvitations.Add(invitation);
                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new InvitationIssueResult(
                    InvitationIssueStatus.Issued, code, invitation.InvitationId, expiresAtUtc);
            }

            return new InvitationIssueResult(InvitationIssueStatus.Unavailable, null, null, null);
        }
        catch
        {
            return new InvitationIssueResult(InvitationIssueStatus.Unavailable, null, null, null);
        }
    }

    private static string GenerateCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = new byte[12];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[14];
        var slot = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i == 4 || i == 8)
            {
                chars[slot++] = '-';
            }

            chars[slot++] = alphabet[bytes[i] % alphabet.Length];
        }

        return new string(chars);
    }
}
