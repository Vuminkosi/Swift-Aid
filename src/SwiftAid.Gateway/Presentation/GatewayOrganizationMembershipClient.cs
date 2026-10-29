using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using SwiftAid.Client.Presentation;
using SwiftAid.Infrastructure.Identity;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Serves the signed-in account's memberships through the server-side session.</summary>
public sealed class GatewayOrganizationMembershipClient(
    AuthenticationStateProvider authenticationStateProvider,
    UserManager<SwiftAidUser> userManager,
    IOrganizationMembershipService membershipService,
    IOrganizationInvitationService invitationService) : IOrganizationMembershipClient
{
    private const string Capability = "organization-membership";

    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly UserManager<SwiftAidUser> _userManager =
        userManager ?? throw new ArgumentNullException(nameof(userManager));
    private readonly IOrganizationMembershipService _membershipService =
        membershipService ?? throw new ArgumentNullException(nameof(membershipService));
    private readonly IOrganizationInvitationService _invitationService =
        invitationService ?? throw new ArgumentNullException(nameof(invitationService));

    public async Task<UiCapabilityResult<IReadOnlyList<MembershipView>>> GetMineAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var actorId = await GetActorIdAsync().ConfigureAwait(false);
            if (actorId is null)
            {
                return Denied<IReadOnlyList<MembershipView>>();
            }

            var memberships = await _membershipService.GetMineAsync(actorId, cancellationToken).ConfigureAwait(false);
            return Success<IReadOnlyList<MembershipView>>(memberships.Select(membership => new MembershipView(
                membership.OrganizationId,
                membership.OrganizationStatus,
                membership.MembershipStatus,
                membership.WorkspaceIds)).ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable<IReadOnlyList<MembershipView>>("Your workspaces are temporarily unavailable.");
        }
    }

    public async Task<UiCapabilityResult<IReadOnlyList<OrganizationDirectoryOption>>> SearchAsync(
        string? query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (await GetActorIdAsync().ConfigureAwait(false) is null)
            {
                return Denied<IReadOnlyList<OrganizationDirectoryOption>>();
            }

            var entries = await _membershipService.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            return Success<IReadOnlyList<OrganizationDirectoryOption>>(entries.Select(entry =>
                new OrganizationDirectoryOption(entry.OrganizationId, entry.Status)).ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable<IReadOnlyList<OrganizationDirectoryOption>>("The organization directory is temporarily unavailable.");
        }
    }

    public async Task<UiCapabilityResult<MembershipRequestView>> RequestAsync(
        string organizationId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var actorId = await GetActorIdAsync().ConfigureAwait(false);
            if (actorId is null)
            {
                return Denied<MembershipRequestView>();
            }

            var submitted = await _membershipService.RequestAsync(actorId, organizationId, reason, cancellationToken)
                .ConfigureAwait(false);
            return submitted.Status switch
            {
                MembershipRequestSubmitStatus.Submitted => Success(new MembershipRequestView(
                    submitted.Request!.RequestId.ToString("D"),
                    submitted.Request.OrganizationId,
                    submitted.Request.Status,
                    submitted.Request.Reason)),
                MembershipRequestSubmitStatus.DuplicatePending => new UiCapabilityResult<MembershipRequestView>(
                    UiCapabilityResultCode.Conflict,
                    null,
                    Metadata(),
                    "You already have a pending request for this organization."),
                MembershipRequestSubmitStatus.AlreadyMember => new UiCapabilityResult<MembershipRequestView>(
                    UiCapabilityResultCode.Conflict,
                    null,
                    Metadata(),
                    "You are already a member of this organization."),
                MembershipRequestSubmitStatus.OrganizationUnavailable => new UiCapabilityResult<MembershipRequestView>(
                    UiCapabilityResultCode.InvalidRequest,
                    null,
                    Metadata(),
                    "Choose an organization from the directory."),
                MembershipRequestSubmitStatus.Invalid => new UiCapabilityResult<MembershipRequestView>(
                    UiCapabilityResultCode.InvalidRequest,
                    null,
                    Metadata(),
                    "Choose an organization to request access."),
                _ => Retryable<MembershipRequestView>("Requesting access is temporarily unavailable.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable<MembershipRequestView>("Requesting access is temporarily unavailable.");
        }
    }

    public async Task<UiCapabilityResult<InvitationRedeemView>> RedeemAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var actorId = await GetActorIdAsync().ConfigureAwait(false);
            if (actorId is null)
            {
                return Denied<InvitationRedeemView>();
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return Invalid<InvitationRedeemView>("Enter the invitation code.");
            }

            var redeemed = await _invitationService.RedeemAsync(actorId, code, cancellationToken)
                .ConfigureAwait(false);
            return redeemed.Status switch
            {
                InvitationRedeemStatus.Redeemed => Success(new InvitationRedeemView(
                    redeemed.OrganizationId ?? string.Empty, "redeemed")),
                InvitationRedeemStatus.AlreadyRequested => Success(new InvitationRedeemView(
                    redeemed.OrganizationId ?? string.Empty, "already-requested")),
                InvitationRedeemStatus.AlreadyMember => Conflict<InvitationRedeemView>(
                    "You are already a member of this organization."),
                InvitationRedeemStatus.Expired => Invalid<InvitationRedeemView>(
                    "This invitation code has expired."),
                InvitationRedeemStatus.Exhausted => Invalid<InvitationRedeemView>(
                    "This invitation code has already been used."),
                InvitationRedeemStatus.OrganizationUnavailable => Invalid<InvitationRedeemView>(
                    "This invitation is not for an available organization."),
                InvitationRedeemStatus.InvalidCode => Invalid<InvitationRedeemView>(
                    "The invitation code is not recognized. Check it and try again."),
                _ => Retryable<InvitationRedeemView>("Redeeming invitations is temporarily unavailable.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Retryable<InvitationRedeemView>(ClientRequestFailure.MessageFor(ex, "Redeeming the invitation"));
        }
        catch (Exception ex) when (ex is HttpRequestException)
        {
            return Retryable<InvitationRedeemView>(ClientRequestFailure.MessageFor(ex, "Redeeming the invitation"));
        }
        catch (Exception)
        {
            return Retryable<InvitationRedeemView>("Redeeming invitations is temporarily unavailable.");
        }
    }

    public async Task<UiCapabilityResult<IReadOnlyList<MembershipRequestView>>> GetMyRequestsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var actorId = await GetActorIdAsync().ConfigureAwait(false);
            if (actorId is null)
            {
                return Denied<IReadOnlyList<MembershipRequestView>>();
            }

            var requests = await _membershipService.GetMyRequestsAsync(actorId, cancellationToken).ConfigureAwait(false);
            return Success<IReadOnlyList<MembershipRequestView>>(requests.Select(request =>
                new MembershipRequestView(
                    request.RequestId.ToString("D"),
                    request.OrganizationId,
                    request.Status,
                    request.Reason)).ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable<IReadOnlyList<MembershipRequestView>>("Your requests are temporarily unavailable.");
        }
    }

    private async Task<string?> GetActorIdAsync()
    {
        var authentication = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        if (authentication.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var user = await _userManager.GetUserAsync(authentication.User).ConfigureAwait(false);
        return user?.Id;
    }

    private static UiCapabilityResult<T> Conflict<T>(string message) => new(
        UiCapabilityResultCode.Conflict,
        default,
        Metadata(),
        message);

    private static UiCapabilityResult<T> Invalid<T>(string message) => new(
        UiCapabilityResultCode.InvalidRequest,
        default,
        Metadata(),
        message);

    private static UiCapabilityResult<T> Denied<T>() => new(
        UiCapabilityResultCode.Denied,
        default,
        Metadata(),
        "Sign in to manage organization access.");

    private static UiCapabilityResult<T> Success<T>(T value) => new(
        UiCapabilityResultCode.Success,
        value,
        Metadata(),
        null);

    private static UiCapabilityResult<T> Retryable<T>(string message) => new(
        UiCapabilityResultCode.Retryable,
        default,
        Metadata(),
        message);

    private static UiCapabilityMetadata Metadata() => new(
        Capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        IsAvailable: true);
}
