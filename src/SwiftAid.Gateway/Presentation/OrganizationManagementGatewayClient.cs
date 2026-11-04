using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Identity;
using SwiftAid.Client.Presentation;
using SwiftAid.Infrastructure.Persistence;
using MembershipStatus = SwiftAid.Infrastructure.Persistence.MembershipRequestStatus;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Organization administration. Organizational roles never modify case access grants.</summary>
public sealed class OrganizationManagementGatewayClient(AuthenticationStateProvider authentication,
    IServiceScopeFactory scopes, OrganizationManagementContext selection, IDataProtectionProvider protection,
    ILogger<OrganizationManagementGatewayClient> logger) : IOrganizationManagementClient
{
    private sealed class AdministrationState
    {
        public AdministrationState() { }
        public OrganizationProfile? Profile { get; set; }
        public Dictionary<string, MemberMetadata> Members { get; set; } = [];
        public Dictionary<string, OrganizationMembershipRequestSubmission> Requests { get; set; } = [];
        public List<OrganizationInvitation> Invitations { get; set; } = [];
        public List<OrganizationAuditEntry> Audit { get; set; } = [];
        public bool SsoRequested { get; set; }
    }
    private sealed record MemberMetadata(OrganizationRole Role, OrganizationCountry Country, OrganizationRegion Region);
    private sealed record OperationContext(SwiftAidDbContext Db, string ActorId, string OrganizationId, AdministrationState State, DateTimeOffset Now);

    public Task<UiCapabilityResult<OrganizationProfile>> GetProfileAsync(CancellationToken cancellationToken = default) =>
        RunAsync(false, false, context => Task.FromResult(context.State.Profile ?? new(context.OrganizationId, OrganizationCountry.Other, OrganizationRegion.Other)), cancellationToken);

    public Task<UiCapabilityResult<OrganizationProfile>> UpdateProfileAsync(OrganizationProfileUpdate update, CancellationToken cancellationToken = default) =>
        RunAsync(true, true, context =>
        {
            Text(update.DisplayName, 120); Text(update.Reason, 500); Region(update.Country, update.Region);
            context.State.Profile = new(update.DisplayName.Trim(), update.Country, update.Region);
            Audit(context, OrganizationAuditAction.ProfileUpdated, update.Reason);
            return Task.FromResult(context.State.Profile);
        }, cancellationToken);

    public Task<UiCapabilityResult<Page<ManagedOrganizationMember>>> ListManagedMembersAsync(PageRequest page, CancellationToken cancellationToken = default) =>
        RunAsync(false, true, async context =>
        {
            ValidatePage(page);
            var query = context.Db.OrganizationMemberships.AsNoTracking().Where(row => row.OrganizationId == context.OrganizationId);
            var count = await query.CountAsync(cancellationToken);
            var rows = await query.OrderBy(row => row.ActorId).Skip((page.PageNumber - 1) * page.PageSize).Take(page.PageSize).ToArrayAsync(cancellationToken);
            var items = new List<ManagedOrganizationMember>();
            foreach (var row in rows) items.Add(await MemberAsync(context, row, cancellationToken));
            return new Page<ManagedOrganizationMember>(items, page.PageNumber, page.PageSize, count, page.PageNumber * page.PageSize < count);
        }, cancellationToken);

    public Task<UiCapabilityResult<Page<OrganizationMembershipRequest>>> ListMembershipRequestsAsync(PageRequest page, CancellationToken cancellationToken = default) =>
        RunAsync(false, true, async context =>
        {
            ValidatePage(page);
            var query = context.Db.MembershipRequests.AsNoTracking().Where(row => row.OrganizationId == context.OrganizationId);
            var count = await query.CountAsync(cancellationToken);
            var rows = await query.OrderByDescending(row => row.CreatedAtUtc).ThenBy(row => row.RequestId)
                .Skip((page.PageNumber - 1) * page.PageSize).Take(page.PageSize).ToArrayAsync(cancellationToken);
            var items = new List<OrganizationMembershipRequest>();
            foreach (var row in rows) items.Add(await RequestAsync(context, row, cancellationToken));
            return new Page<OrganizationMembershipRequest>(items, page.PageNumber, page.PageSize, count, page.PageNumber * page.PageSize < count);
        }, cancellationToken);

    public Task<UiCapabilityResult<OrganizationMembershipRequest>> SubmitMembershipRequestAsync(OrganizationMembershipRequestSubmission request,
        CancellationToken cancellationToken = default) => RunAsync(true, false, async context =>
        {
            Text(request.Reason, 500); Region(request.Country, request.Region); Role(request.RequestedRole);
            var existing = await context.Db.MembershipRequests.SingleOrDefaultAsync(row => row.OrganizationId == context.OrganizationId &&
                row.ActorId == context.ActorId && row.Status == MembershipStatus.Pending, cancellationToken);
            if (existing is not null) return await RequestAsync(context, existing, cancellationToken);
            var row = new MembershipRequestEntity { RequestId = Guid.NewGuid(), ActorId = context.ActorId,
                OrganizationId = context.OrganizationId, Status = MembershipStatus.Pending, Reason = request.Reason.Trim(),
                CreatedAtUtc = context.Now, UpdatedAtUtc = context.Now };
            context.Db.MembershipRequests.Add(row);
            context.State.Requests[row.RequestId.ToString("D")] = request with { RequesterName = "" };
            Audit(context, OrganizationAuditAction.MembershipRequested, request.Reason);
            return await RequestAsync(context, row, cancellationToken);
        }, cancellationToken, allowApplicant: true);

    public Task<UiCapabilityResult<OrganizationMembershipRequest>> ReviewMembershipRequestAsync(OrganizationMembershipRequestReview review,
        CancellationToken cancellationToken = default) => RunAsync(true, true, async context =>
        {
            Text(review.Reason, 500);
            if (!Guid.TryParse(review.RequestId, out var id)) throw new ArgumentException("Invalid request.");
            var row = await context.Db.MembershipRequests.SingleOrDefaultAsync(item => item.RequestId == id && item.OrganizationId == context.OrganizationId, cancellationToken)
                ?? throw new UnauthorizedAccessException();
            var expected = review.Approve ? MembershipStatus.Approved : MembershipStatus.Declined;
            if (row.Status != MembershipStatus.Pending)
            {
                if (row.Status != expected) throw new InvalidOperationException("The request was already reviewed.");
                return await RequestAsync(context, row, cancellationToken);
            }
            if (review.Approve)
            {
                var member = await context.Db.OrganizationMemberships.SingleOrDefaultAsync(item => item.ActorId == row.ActorId && item.OrganizationId == context.OrganizationId, cancellationToken);
                if (member is null)
                {
                    member = new OrganizationMembershipEntity { MembershipId = Guid.NewGuid(), ActorId = row.ActorId,
                        OrganizationId = context.OrganizationId, GrantedByActorId = context.ActorId, Version = 0 };
                    context.Db.OrganizationMemberships.Add(member);
                }
                else if (member.Status != PersistentMembershipStatus.Active || member.ExpiresAtUtc <= context.Now)
                {
                    throw new InvalidOperationException("Inactive membership requires a separate access review before restoration.");
                }
                member.Status = PersistentMembershipStatus.Active;
                member.ValidFromUtc = context.Now; member.ExpiresAtUtc = null; member.RevokedByActorId = null;
                member.GrantedByActorId = context.ActorId; member.ChangeReason = review.Reason.Trim(); member.Version++;
                var metadata = context.State.Requests.GetValueOrDefault(review.RequestId);
                context.State.Members[member.MembershipId.ToString("D")] = metadata is null
                    ? new(OrganizationRole.Member, OrganizationCountry.Other, OrganizationRegion.Other)
                    : new(metadata.RequestedRole, metadata.Country, metadata.Region);
                await AdvanceEpochAsync(context, row.ActorId, cancellationToken);
            }
            row.Status = expected; row.UpdatedAtUtc = context.Now;
            Audit(context, review.Approve ? OrganizationAuditAction.MembershipApproved : OrganizationAuditAction.MembershipDeclined, review.Reason);
            return await RequestAsync(context, row, cancellationToken);
        }, cancellationToken);

    public Task<UiCapabilityResult<ManagedOrganizationMember>> UpdateMemberAsync(OrganizationMemberChange change,
        CancellationToken cancellationToken = default) => RunAsync(true, true, async context =>
        {
            Text(change.Reason, 500);
            var member = await FindMemberAsync(context, change.MemberId, cancellationToken);
            if (member.Status != PersistentMembershipStatus.Active) throw new InvalidOperationException("Membership is not active.");
            if (change.Role.HasValue && await context.Db.OrganizationOwners.AnyAsync(row => row.OrganizationId == context.OrganizationId &&
                row.ActorId == member.ActorId && row.RevokedAtUtc == null, cancellationToken))
                throw new InvalidOperationException("Ownership is managed separately from member roles.");
            var previous = context.State.Members.GetValueOrDefault(change.MemberId) ?? new(OrganizationRole.Member, OrganizationCountry.Other, OrganizationRegion.Other);
            var next = new MemberMetadata(change.Role ?? previous.Role, change.Country ?? previous.Country, change.Region ?? previous.Region);
            Role(next.Role); Region(next.Country, next.Region);
            context.State.Members[change.MemberId] = next;
            Audit(context, change.Role.HasValue ? OrganizationAuditAction.MemberRoleChanged : OrganizationAuditAction.MemberRegionChanged, change.Reason);
            return await MemberAsync(context, member, cancellationToken);
        }, cancellationToken);

    public Task<UiCapabilityResult<ManagedOrganizationMember>> RevokeMembershipAsync(OrganizationMembershipRevocation revocation,
        CancellationToken cancellationToken = default) => RunAsync(true, true, async context =>
        {
            Text(revocation.Reason, 500);
            var member = await FindMemberAsync(context, revocation.MemberId, cancellationToken);
            if (await context.Db.OrganizationOwners.AnyAsync(row => row.OrganizationId == context.OrganizationId && row.ActorId == member.ActorId && row.RevokedAtUtc == null, cancellationToken))
                throw new InvalidOperationException("Transfer organization ownership before revoking this membership.");
            if (member.Status != PersistentMembershipStatus.Revoked)
            {
                member.Status = PersistentMembershipStatus.Revoked; member.RevokedByActorId = context.ActorId;
                member.ChangeReason = revocation.Reason.Trim(); member.Version++;
                await AdvanceEpochAsync(context, member.ActorId, cancellationToken);
                Audit(context, OrganizationAuditAction.MembershipRevoked, revocation.Reason);
            }
            return await MemberAsync(context, member, cancellationToken);
        }, cancellationToken);

    // The existing surface explicitly requests invitation previews. No mail or membership grant is implied.
    public Task<UiCapabilityResult<OrganizationInvitation>> SendInvitationAsync(OrganizationInvitationRequest invitation,
        CancellationToken cancellationToken = default) => RunAsync(true, true, context =>
        {
            Text(invitation.Email, 254); Text(invitation.Reason, 500); Role(invitation.Role); Region(invitation.Country, invitation.Region);
            if (!System.Net.Mail.MailAddress.TryCreate(invitation.Email, out var address) || address.Address != invitation.Email.Trim())
                throw new ArgumentException("Invalid email address.");
            var result = new OrganizationInvitation(Guid.NewGuid().ToString("D"), invitation.Email.Trim(), invitation.Role.ToString(), "Preview — not sent", false);
            context.State.Invitations.Add(result);
            Audit(context, OrganizationAuditAction.InvitationPreviewCreated, invitation.Reason);
            return Task.FromResult(result);
        }, cancellationToken);

    public Task<UiCapabilityResult<OrganizationSsoConfiguration>> GetSsoConfigurationAsync(CancellationToken cancellationToken = default) =>
        RunAsync(false, true, context => Task.FromResult(Sso(context)), cancellationToken);

    public Task<UiCapabilityResult<OrganizationSsoConfiguration>> RequestSsoSetupAsync(string reason, CancellationToken cancellationToken = default) =>
        RunAsync(true, true, context =>
        {
            Text(reason, 500); context.State.SsoRequested = true;
            Audit(context, OrganizationAuditAction.SsoConfigurationChanged, reason);
            return Task.FromResult(Sso(context));
        }, cancellationToken);

    public Task<UiCapabilityResult<IReadOnlyList<OrganizationAuditEntry>>> GetAuditAsync(CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<OrganizationAuditEntry>>(false, true, context => Task.FromResult<IReadOnlyList<OrganizationAuditEntry>>(
            context.State.Audit.OrderByDescending(row => row.OccurredAtUtc).Take(100).ToArray()), cancellationToken);

    private async Task<UiCapabilityResult<T>> RunAsync<T>(bool write, bool ownerRequired, Func<OperationContext, Task<T>> operation,
        CancellationToken ct, bool allowApplicant = false)
    {
        var organizationId = selection.OrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId) || organizationId.Length > 160) return Result<T>(UiCapabilityResultCode.InvalidRequest);
        try
        {
            var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync()).User);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SwiftAidDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var now = DateTimeOffset.UtcNow;
            var epoch = await db.ActorAccessStates.FromSqlInterpolated($"SELECT * FROM actor_access_states WHERE actor_id = {actor.ActorId} FOR SHARE")
                .SingleOrDefaultAsync(ct);
            var current = await db.Actors.AnyAsync(row => row.ActorId == actor.ActorId && row.Status == PersistentActorStatus.Active &&
                (row.ExpiresAtUtc == null || row.ExpiresAtUtc > now), ct) && epoch?.AccessEpoch == actor.AccessEpoch;
            if (!current) return Result<T>(UiCapabilityResultCode.Denied);
            var organization = await db.Organizations.FromSqlInterpolated($"SELECT * FROM organizations WHERE organization_id = {organizationId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (organization?.Status != PersistentResourceStatus.Active) return Result<T>(UiCapabilityResultCode.Denied);
            var owner = await scope.ServiceProvider.GetRequiredService<IOrganizationOwnerAuthorizer>().IsActiveOwnerAsync(actor.ActorId, organizationId, ct);
            if (ownerRequired && !owner) return Result<T>(UiCapabilityResultCode.Denied);
            if (!owner && !allowApplicant && !await db.OrganizationMemberships.AnyAsync(row => row.ActorId == actor.ActorId &&
                row.OrganizationId == organizationId && row.Status == PersistentMembershipStatus.Active && row.ValidFromUtc <= now &&
                (row.ExpiresAtUtc == null || row.ExpiresAtUtc > now), ct)) return Result<T>(UiCapabilityResultCode.Denied);
            var record = await db.Set<OrganizationAdministrationEntity>().SingleOrDefaultAsync(row => row.OrganizationId == organizationId, ct);
            var protector = protection.CreateProtector("SwiftAid.OrganizationAdministration.v1", organizationId);
            var state = record is null ? new AdministrationState() : JsonSerializer.Deserialize<AdministrationState>(protector.Unprotect(record.ProtectedState))
                ?? throw new InvalidDataException("Invalid organization administration state.");
            var value = await operation(new(db, actor.ActorId, organizationId, state, now));
            if (write)
            {
                var json = JsonSerializer.Serialize(state);
                if (System.Text.Encoding.UTF8.GetByteCount(json) > 1_048_576) throw new InvalidOperationException("Organization administration storage requires archival.");
                if (record is null) { record = new() { OrganizationId = organizationId }; db.Add(record); }
                record.ProtectedState = protector.Protect(json); record.Version++;
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return Result(UiCapabilityResultCode.Success, value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return Result<T>(UiCapabilityResultCode.Denied); }
        catch (ArgumentException) { return Result<T>(UiCapabilityResultCode.InvalidRequest); }
        catch (DbUpdateConcurrencyException) { return Result<T>(UiCapabilityResultCode.Conflict); }
        catch (InvalidOperationException) { return Result<T>(UiCapabilityResultCode.Conflict); }
        catch (Exception error) { logger.LogWarning(error, "Organization administration request failed."); return Result<T>(UiCapabilityResultCode.Unavailable); }
    }

    private static async Task<OrganizationMembershipEntity> FindMemberAsync(OperationContext context, string id, CancellationToken ct) =>
        Guid.TryParse(id, out var key) ? await context.Db.OrganizationMemberships.SingleOrDefaultAsync(row => row.MembershipId == key && row.OrganizationId == context.OrganizationId, ct)
            ?? throw new UnauthorizedAccessException() : throw new ArgumentException("Invalid membership identifier.");

    private static async Task<ManagedOrganizationMember> MemberAsync(OperationContext context, OrganizationMembershipEntity row, CancellationToken ct)
    {
        var metadata = context.State.Members.GetValueOrDefault(row.MembershipId.ToString("D")) ?? new(OrganizationRole.Member, OrganizationCountry.Other, OrganizationRegion.Other);
        var owner = await context.Db.OrganizationOwners.AnyAsync(item => item.OrganizationId == context.OrganizationId && item.ActorId == row.ActorId && item.RevokedAtUtc == null, ct);
        return new(row.MembershipId.ToString("D"), await LabelAsync(context.Db, row.ActorId, ct), owner ? OrganizationRole.Administrator : metadata.Role,
            metadata.Country, metadata.Region, row.Status.ToString());
    }
    private static async Task<OrganizationMembershipRequest> RequestAsync(OperationContext context, MembershipRequestEntity row, CancellationToken ct)
    {
        var details = context.State.Requests.GetValueOrDefault(row.RequestId.ToString("D"));
        return new(row.RequestId.ToString("D"), await LabelAsync(context.Db, row.ActorId, ct), details?.Country ?? OrganizationCountry.Other,
            details?.Region ?? OrganizationRegion.Other, details?.RequestedRole ?? OrganizationRole.Member,
            row.Status switch { MembershipStatus.Approved => OrganizationMembershipRequestStatus.Approved, MembershipStatus.Declined => OrganizationMembershipRequestStatus.Declined, _ => OrganizationMembershipRequestStatus.Pending }, row.CreatedAtUtc);
    }
    private static async Task<string> LabelAsync(SwiftAidDbContext db, string actorId, CancellationToken ct) =>
        await db.UserProfiles.Where(row => row.UserId == actorId).Select(row => row.DisplayName).SingleOrDefaultAsync(ct) ?? "Member";
    private static async Task AdvanceEpochAsync(OperationContext context, string actorId, CancellationToken ct) =>
        _ = await context.Db.ActorAccessStates.Where(row => row.ActorId == actorId).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.AccessEpoch, row => row.AccessEpoch + 1).SetProperty(row => row.Version, row => row.Version + 1)
            .SetProperty(row => row.UpdatedAtUtc, context.Now), ct);
    private static void Audit(OperationContext context, OrganizationAuditAction action, string reason) =>
        context.State.Audit.Add(new(action, reason.Trim(), context.Now));
    private static OrganizationSsoConfiguration Sso(OperationContext context) => new(context.State.SsoRequested ? OrganizationSsoStatus.SetupPending : OrganizationSsoStatus.NotConfigured,
        context.State.SsoRequested ? "Setup request recorded. A verified identity-provider configuration is still required." : "No organization SSO setup request has been recorded.", false);
    private static void Text(string value, int limit) { if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value.Any(char.IsControl)) throw new ArgumentException("Invalid text."); }
    private static void Role(OrganizationRole role) { if (!Enum.IsDefined(role) || role == OrganizationRole.Administrator) throw new ArgumentException("Ownership is managed separately."); }
    private static void Region(OrganizationCountry country, OrganizationRegion region)
    {
        if (!Enum.IsDefined(country) || !Enum.IsDefined(region) || !(country switch
        {
            OrganizationCountry.Zimbabwe => region is >= OrganizationRegion.Harare and <= OrganizationRegion.Midlands,
            OrganizationCountry.Eswatini => region is >= OrganizationRegion.Hhohho and <= OrganizationRegion.Shiselweni,
            _ => region == OrganizationRegion.Other
        })) throw new ArgumentException("Region does not belong to the country.");
    }
    private static void ValidatePage(PageRequest page) { if (!page.IsValid || page.PageNumber > 100000) throw new ArgumentException("Invalid page."); }
    private static UiCapabilityResult<T> Result<T>(UiCapabilityResultCode code, T? value = default) => new(code, value,
        new("organization-management", UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, code == UiCapabilityResultCode.Success));
}
