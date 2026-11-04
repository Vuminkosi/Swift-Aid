using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SwiftAid.Client.Presentation;

public sealed class OrganizationManagementHttpClient(HttpClient http, OrganizationManagementContext selection) : IOrganizationManagementClient
{
    public Task<UiCapabilityResult<OrganizationProfile>> GetProfileAsync(CancellationToken cancellationToken = default) => Send<OrganizationProfile>("profile", null, cancellationToken);
    public Task<UiCapabilityResult<OrganizationProfile>> UpdateProfileAsync(OrganizationProfileUpdate update, CancellationToken cancellationToken = default) => Send<OrganizationProfile>("profile", update, cancellationToken);
    public Task<UiCapabilityResult<Page<OrganizationMembershipRequest>>> ListMembershipRequestsAsync(PageRequest page, CancellationToken cancellationToken = default) => Send<Page<OrganizationMembershipRequest>>($"requests?page={page.PageNumber}&pageSize={page.PageSize}", null, cancellationToken);
    public Task<UiCapabilityResult<Page<ManagedOrganizationMember>>> ListManagedMembersAsync(PageRequest page, CancellationToken cancellationToken = default) => Send<Page<ManagedOrganizationMember>>($"members?page={page.PageNumber}&pageSize={page.PageSize}", null, cancellationToken);
    public Task<UiCapabilityResult<OrganizationMembershipRequest>> SubmitMembershipRequestAsync(OrganizationMembershipRequestSubmission request, CancellationToken cancellationToken = default) => Send<OrganizationMembershipRequest>("requests", request, cancellationToken);
    public Task<UiCapabilityResult<OrganizationMembershipRequest>> ReviewMembershipRequestAsync(OrganizationMembershipRequestReview review, CancellationToken cancellationToken = default) => Send<OrganizationMembershipRequest>("requests/review", review, cancellationToken);
    public Task<UiCapabilityResult<OrganizationInvitation>> SendInvitationAsync(OrganizationInvitationRequest invitation, CancellationToken cancellationToken = default) => Send<OrganizationInvitation>("invitations", invitation, cancellationToken);
    public Task<UiCapabilityResult<ManagedOrganizationMember>> UpdateMemberAsync(OrganizationMemberChange change, CancellationToken cancellationToken = default) => Send<ManagedOrganizationMember>("members", change, cancellationToken);
    public Task<UiCapabilityResult<ManagedOrganizationMember>> RevokeMembershipAsync(OrganizationMembershipRevocation revocation, CancellationToken cancellationToken = default) => Send<ManagedOrganizationMember>("members/revoke", revocation, cancellationToken);
    public Task<UiCapabilityResult<OrganizationSsoConfiguration>> GetSsoConfigurationAsync(CancellationToken cancellationToken = default) => Send<OrganizationSsoConfiguration>("sso", null, cancellationToken);
    public Task<UiCapabilityResult<OrganizationSsoConfiguration>> RequestSsoSetupAsync(string reason, CancellationToken cancellationToken = default) => Send<OrganizationSsoConfiguration>("sso", new OrganizationSsoSetupRequest(reason), cancellationToken);
    public Task<UiCapabilityResult<IReadOnlyList<OrganizationAuditEntry>>> GetAuditAsync(CancellationToken cancellationToken = default) => Send<IReadOnlyList<OrganizationAuditEntry>>("audit", null, cancellationToken);

    private async Task<UiCapabilityResult<T>> Send<T>(string route, object? payload, CancellationToken ct)
    {
        var id = selection.OrganizationId;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 160) return Failure<T>(UiCapabilityResultCode.InvalidRequest);
        try
        {
            using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post,
                $"api/v1/organizations/{Uri.EscapeDataString(id)}/management/{route}");
            if (payload is not null) request.Content = JsonContent.Create(payload, payload.GetType());
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return Failure<T>(response.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => UiCapabilityResultCode.Denied,
                HttpStatusCode.BadRequest => UiCapabilityResultCode.InvalidRequest,
                HttpStatusCode.Conflict => UiCapabilityResultCode.Conflict,
                _ => UiCapabilityResultCode.Unavailable
            });
            return await response.Content.ReadFromJsonAsync<UiCapabilityResult<T>>(cancellationToken: ct) ?? Failure<T>(UiCapabilityResultCode.Unavailable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        { return Failure<T>(UiCapabilityResultCode.Unavailable); }
    }
    private static UiCapabilityResult<T> Failure<T>(UiCapabilityResultCode code) => new(code, default,
        UiCapabilityResults.UnavailableMetadata("organization-management", "Organization administration could not be loaded. Please retry."));
}

public sealed record OrganizationSsoSetupRequest(string Reason);