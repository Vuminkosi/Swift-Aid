using System.Net;
using System.Text.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Mobile.Services.Http;

namespace SwiftAid.Mobile.Services.Account;

/// <summary>Member-side organization access through the gateway cookie session.</summary>
public sealed class HttpOrganizationMembershipClient(CookieSessionHttpClient session) : IOrganizationMembershipClient
{
    private const string Capability = "organization-membership";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CookieSessionHttpClient _session = session ?? throw new ArgumentNullException(nameof(session));

    public async Task<UiCapabilityResult<IReadOnlyList<MembershipView>>> GetMineAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var response = await _session.GetAsync("/auth/organizations/mine", cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Denied<IReadOnlyList<MembershipView>>();
            }

            if (!response.IsSuccessStatusCode)
            {
                return Retryable<IReadOnlyList<MembershipView>>("Your workspaces are temporarily unavailable.");
            }

            var payload = await ReadAsync<MembershipListPayload>(response, cancellationToken).ConfigureAwait(false);
            return payload is null
                ? Retryable<IReadOnlyList<MembershipView>>("Your workspaces are temporarily unavailable.")
                : Success<IReadOnlyList<MembershipView>>(payload.Memberships.Select(membership =>
                    new MembershipView(
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
            var path = string.IsNullOrWhiteSpace(query)
                ? "/auth/organizations/directory"
                : $"/auth/organizations/directory?search={Uri.EscapeDataString(query.Trim())}";
            using var response = await _session.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Denied<IReadOnlyList<OrganizationDirectoryOption>>();
            }

            if (!response.IsSuccessStatusCode)
            {
                return Retryable<IReadOnlyList<OrganizationDirectoryOption>>("The organization directory is temporarily unavailable.");
            }

            var payload = await ReadAsync<OrganizationDirectoryPayload>(response, cancellationToken).ConfigureAwait(false);
            return payload is null
                ? Retryable<IReadOnlyList<OrganizationDirectoryOption>>("The organization directory is temporarily unavailable.")
                : Success<IReadOnlyList<OrganizationDirectoryOption>>(payload.Organizations.Select(entry =>
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
            var token = await _session.GetAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _session.PostJsonAsync(
                "/auth/organizations/requests",
                new { organizationId, reason },
                token,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Denied<MembershipRequestView>();
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var message = await ReadMessageAsync(response, cancellationToken).ConfigureAwait(false)
                    ?? "You already have a pending request for this organization.";
                return new UiCapabilityResult<MembershipRequestView>(
                    UiCapabilityResultCode.Conflict, null, Metadata(), message);
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var message = await ReadMessageAsync(response, cancellationToken).ConfigureAwait(false)
                    ?? "Choose an organization to request access.";
                return new UiCapabilityResult<MembershipRequestView>(
                    UiCapabilityResultCode.InvalidRequest, null, Metadata(), message);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Retryable<MembershipRequestView>("Requesting access is temporarily unavailable.");
            }

            var payload = await ReadAsync<MembershipRequestPayload>(response, cancellationToken).ConfigureAwait(false);
            return payload is null
                ? Retryable<MembershipRequestView>("Requesting access is temporarily unavailable.")
                : Success(new MembershipRequestView(
                    payload.RequestId.ToString("D"),
                    payload.OrganizationId,
                    payload.Status,
                    payload.Reason));
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
        ArgumentNullException.ThrowIfNull(code);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var token = await _session.GetAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _session.PostJsonAsync(
                "/auth/organizations/invitations/redeem",
                new { code },
                token,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Denied<InvitationRedeemView>();
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var conflict = await ReadMessageAsync(response, cancellationToken).ConfigureAwait(false)
                    ?? "You are already a member of this organization.";
                return new UiCapabilityResult<InvitationRedeemView>(
                    UiCapabilityResultCode.Conflict, null, Metadata(), conflict);
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var message = await ReadMessageAsync(response, cancellationToken).ConfigureAwait(false)
                    ?? "The invitation code is not recognized. Check it and try again.";
                return new UiCapabilityResult<InvitationRedeemView>(
                    UiCapabilityResultCode.InvalidRequest, null, Metadata(), message);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Retryable<InvitationRedeemView>("Redeeming invitations is temporarily unavailable.");
            }

            var payload = await ReadAsync<InvitationRedeemPayload>(response, cancellationToken).ConfigureAwait(false);
            return payload is null
                ? Retryable<InvitationRedeemView>("Redeeming invitations is temporarily unavailable.")
                : Success(new InvitationRedeemView(payload.OrganizationId, payload.Status));
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
            using var response = await _session.GetAsync("/auth/organizations/requests/mine", cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Denied<IReadOnlyList<MembershipRequestView>>();
            }

            if (!response.IsSuccessStatusCode)
            {
                return Retryable<IReadOnlyList<MembershipRequestView>>("Your requests are temporarily unavailable.");
            }

            var payload = await ReadAsync<MembershipRequestListPayload>(response, cancellationToken).ConfigureAwait(false);
            return payload is null
                ? Retryable<IReadOnlyList<MembershipRequestView>>("Your requests are temporarily unavailable.")
                : Success<IReadOnlyList<MembershipRequestView>>(payload.Requests.Select(request =>
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

    private static async Task<T?> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static async Task<string?> ReadMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private sealed record MembershipListPayload(IReadOnlyList<MembershipPayload> Memberships);

    private sealed record MembershipPayload(
        string OrganizationId,
        string OrganizationStatus,
        string MembershipStatus,
        IReadOnlyList<string> WorkspaceIds,
        DateTimeOffset ValidFromUtc,
        DateTimeOffset? ExpiresAtUtc);

    private sealed record OrganizationDirectoryPayload(IReadOnlyList<OrganizationDirectoryOptionPayload> Organizations);

    private sealed record OrganizationDirectoryOptionPayload(string OrganizationId, string Status);

    private sealed record MembershipRequestListPayload(IReadOnlyList<MembershipRequestPayload> Requests);

    private sealed record InvitationRedeemPayload(string OrganizationId, string Status);

    private sealed record MembershipRequestPayload(
        Guid RequestId,
        string OrganizationId,
        string Status,
        string? Reason,
        DateTimeOffset CreatedAtUtc);
}
