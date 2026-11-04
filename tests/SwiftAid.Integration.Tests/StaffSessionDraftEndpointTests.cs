using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Client.Presentation;
using SwiftAid.Application.Identity;
using SwiftAid.Application.Sessions;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Gateway.Endpoints;
using DomainSessions = SwiftAid.Domain.Sessions;

namespace SwiftAid.Integration.Tests;

public sealed class StaffSessionDraftEndpointTests
{
    [Fact]
    public async Task ReservationAndCreateRetainOneServerIssuedIdentity()
    {
        var access = new Access(); var commands = new Commands();
        await using var app = await StartAsync(access, commands);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        var client = new StaffSessionEditorHttpClient(http);
        var reservation = (await client.ReserveDraftAsync("org-a")).Value!;
        Assert.NotEqual(Guid.Empty, reservation.SessionId);
        var command = Draft(reservation.SessionId);
        var result = await client.ExecuteAsync(command);
        Assert.True(result.Succeeded);
        Assert.Equal(reservation.SessionId, result.Value!.SessionId);
        Assert.Equal("actor-1", commands.Actor);
        Assert.Equal(reservation.SessionId, commands.Command!.SessionId);
        Assert.Equal(1, commands.Accepted);
        Assert.False((await client.ExecuteAsync(command)).Succeeded);
        using var replay = await http.PostAsJsonAsync("/api/v1/session-editor/drafts", CreatePayload(reservation.SessionId, reservation.Token, "org-a"));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal(1, commands.Accepted);
    }

    [Fact]
    public async Task TicketCannotBeUsedByAnotherActorOrForAnotherOrganizationOrIdentifier()
    {
        var access = new Access(); var commands = new Commands();
        await using var app = await StartAsync(access, commands);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var reserve = await http.PostAsJsonAsync("/api/v1/session-editor/draft-reservations", new { organizationId = "org-a" });
        Assert.True(reserve.Headers.CacheControl?.NoStore);
        var ticket = (await reserve.Content.ReadFromJsonAsync<SessionDraftReservation>())!;
        var payload = CreatePayload(ticket.SessionId, ticket.Token, "org-b");
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/v1/session-editor/drafts", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/v1/session-editor/drafts", CreatePayload(Guid.NewGuid(), ticket.Token, "org-a"))).StatusCode);
        access.Actor = "actor-2";
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/v1/session-editor/drafts", CreatePayload(ticket.SessionId, ticket.Token, "org-a"))).StatusCode);
        Assert.Equal(0, commands.Accepted);
    }

    [Fact]
    public async Task CookieRequestsRequireAntiforgeryAndNonownersCannotReserve()
    {
        var access = new Access(); var commands = new Commands();
        await using var app = await StartAsync(access, commands);
        using var http = app.GetTestClient();
        using var cookie = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session-editor/draft-reservations")
            { Content = JsonContent.Create(new { organizationId = "org-a" }) };
        TestAuthenticationHandler.AddCookieAuthentication(cookie);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(cookie)).StatusCode);
        using var mixed = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session-editor/draft-reservations")
            { Content = JsonContent.Create(new { organizationId = "org-a" }) };
        TestAuthenticationHandler.AddCookieAuthentication(mixed);
        mixed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(mixed)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        access.Allowed = false;
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/v1/session-editor/draft-reservations", new { organizationId = "org-a" })).StatusCode);
        Assert.Equal(0, commands.Accepted);
    }

    [Fact]
    public async Task CommitThenLostResponseCanBeRetriedWithoutCreatingAnotherDraft()
    {
        var access = new Access(); var commands = new Commands();
        await using var app = await StartAsync(access, commands);
        using var http = new HttpClient(new DropAcceptedCreate(app.GetTestServer().CreateHandler())) { BaseAddress = new Uri("http://localhost") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        var client = new StaffSessionEditorHttpClient(http);
        var reservation = (await client.ReserveDraftAsync("org-a")).Value!;
        var draft = Draft(reservation.SessionId);
        Assert.Equal(UiCapabilityResultCode.Unavailable, (await client.ExecuteAsync(draft)).Code);
        Assert.Equal(1, commands.Accepted);
        Assert.Equal(UiCapabilityResultCode.Conflict, (await client.ExecuteAsync(draft)).Code);
        Assert.Equal(1, commands.Accepted);
        Assert.Equal(reservation.SessionId, commands.Command!.SessionId);
    }

    [Fact]
    public async Task RevokedOwnershipIsCheckedAgainAtCreate()
    {
        var access = new Access(); var commands = new Commands();
        await using var app = await StartAsync(access, commands);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        var client = new StaffSessionEditorHttpClient(http);
        var reservation = (await client.ReserveDraftAsync("org-a")).Value!;
        commands.Deny = true;
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.ExecuteAsync(Draft(reservation.SessionId))).Code);
        Assert.Equal(0, commands.Accepted);
    }

    [Fact]
    public async Task StaleSessionIsRejectedAfterReservation()
    {
        var access = new Access(); var commands = new Commands();
        await using var app = await StartAsync(access, commands);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        var client = new StaffSessionEditorHttpClient(http);
        var reservation = (await client.ReserveDraftAsync("org-a")).Value!;
        access.Current = false;
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.ExecuteAsync(Draft(reservation.SessionId))).Code);
        Assert.Equal(0, commands.Accepted);
    }

    [Fact]
    public async Task RealSessionServiceRechecksOwnershipAndRejectsDuplicateIdentity()
    {
        var access = new Access(); var store = new InMemorySessionStore(); var owner = new Owner();
        var service = new CommunitySessionService(store, ownerAuthorizer: owner);
        await using var app = await StartAsync(access, service);
        using var http = new HttpClient(new DropAcceptedCreate(app.GetTestServer().CreateHandler())) { BaseAddress = new Uri("http://localhost") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        var client = new StaffSessionEditorHttpClient(http);
        var first = (await client.ReserveDraftAsync("org-a")).Value!;
        Assert.Equal(UiCapabilityResultCode.Unavailable, (await client.ExecuteAsync(Draft(first.SessionId))).Code);
        Assert.Equal(UiCapabilityResultCode.Conflict, (await client.ExecuteAsync(Draft(first.SessionId))).Code);
        Assert.Equal(1, store.Count);
        Assert.NotNull(await service.GetStaffAsync(first.SessionId, "actor-1", CancellationToken.None));
        var second = (await client.ReserveDraftAsync("org-a")).Value!;
        owner.Allowed = false;
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.ExecuteAsync(Draft(second.SessionId))).Code);
        Assert.Equal(1, store.Count);
    }

    private static CreateSessionDraftCommand Draft(Guid id) => new(id, "org-a", "Title", "Summary", "Audience", [], "SZ", "region", "human",
        "Presenter", "English", DateTimeOffset.UtcNow.AddDays(1), "UTC", 60, null, [new("source", "v1")], SessionVisibility.Public,
        SessionChannelType.None, null, [], "reason", DateTimeOffset.UtcNow);

    private static object CreatePayload(Guid id, string token, string organizationId) => new
    {
        sessionId = id, reservationToken = token,
        draft = new { organizationId, title = "Title", summary = "Summary", audienceDescription = "Audience", topicTags = Array.Empty<string>(),
            countryCode = "SZ", regionId = "region", sector = "human", presenter = "Presenter", language = "English",
            startUtc = DateTimeOffset.UtcNow.AddDays(1), timeZoneId = "UTC", durationMinutes = 60, capacity = (int?)null,
            materials = new[] { new { materialId = "source", sourceVersion = "v1", rightsEvidence = (string?)null } }, visibility = "Public", channelType = "None", channelLink = (string?)null,
            reminderLeadHours = Array.Empty<int>(), reason = "reason" }
    };

    private static async Task<WebApplication> StartAsync(Access access, ICommunitySessionService commands)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDataProtection();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
        }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(IdentityConstants.ApplicationScheme, _ => { })
          .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IStaffSessionEditorReadRequest>(access);
        builder.Services.AddSingleton<IWorkspaceAccessRequest>(access);
        builder.Services.AddSingleton<ICommunitySessionService>(commands);
        var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapStaffSessionDraftEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class Access : IStaffSessionEditorReadRequest, IWorkspaceAccessRequest
    {
        public string Actor { get; set; } = "actor-1";
        public bool Allowed { get; set; } = true;
        public bool Current { get; set; } = true;
        public Task<UiCapabilityResult<WorkspaceAccessState>> GetAsync(ClaimsPrincipal principal, string? workspace, CancellationToken ct) =>
            Task.FromResult(new UiCapabilityResult<WorkspaceAccessState>(Current ? UiCapabilityResultCode.Success : UiCapabilityResultCode.Denied,
                Current ? new WorkspaceAccessState([new("org-a-general", "org-a-general")], "org-a-general") : null, Metadata()));
        public Task<UiCapabilityResult<string>> ActorAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.FromResult(Result(Actor));
        public Task<UiCapabilityResult<StaffSessionManagementCapabilities>> CapabilitiesAsync(ClaimsPrincipal principal, Guid? sessionId, long? version, CancellationToken ct) =>
            Task.FromResult(Result(new StaffSessionManagementCapabilities(Allowed, false, false, false, false, false, null, null, null, true,
                OwnerOrganizationIds: Allowed ? ["org-a"] : [])));
        public Task<UiCapabilityResult<CommunitySessionView>> SessionAsync(ClaimsPrincipal principal, Guid sessionId, CancellationToken ct) =>
            Task.FromResult(new UiCapabilityResult<CommunitySessionView>(UiCapabilityResultCode.Denied, null, Metadata()));
        private static UiCapabilityResult<T> Result<T>(T value) => new(UiCapabilityResultCode.Success, value, Metadata());
        private static UiCapabilityMetadata Metadata() => new("session-editor", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true);
    }

    private sealed class Commands : ICommunitySessionService
    {
        public CreateSessionDraftCommand? Command { get; private set; }
        public string? Actor { get; private set; }
        public int Accepted { get; private set; }
        public bool Deny { get; set; }
        public Task<SessionCommandResult> ExecuteAsync(SessionCommand command, string actorId, CancellationToken ct)
        {
            if (Deny) throw new UnauthorizedAccessException();
            if (Accepted > 0) throw new InvalidOperationException("version conflict");
            Command = (CreateSessionDraftCommand)command; Actor = actorId; Accepted++;
            return Task.FromResult(new SessionCommandResult(command.SessionId, 1, "Draft"));
        }
        public Task<CommunitySessionView?> GetStaffAsync(Guid sessionId, string actorId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PublicSessionView>> ListPublicAsync(int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<PublicSessionPage> QueryPublicAsync(PublicSessionQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<PublicSessionView?> GetPublicAsync(Guid sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetIcsAsync(Guid sessionId, DateTimeOffset generatedAtUtc, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetInvitationTextAsync(Guid sessionId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class DropAcceptedCreate(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private bool _dropped;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!_dropped && request.RequestUri?.AbsolutePath == "/api/v1/session-editor/drafts" && response.IsSuccessStatusCode)
            {
                _dropped = true;
                response.Dispose();
                throw new HttpRequestException("Synthetic lost response after commit.");
            }
            return response;
        }
    }

    private sealed class Owner : IOrganizationOwnerAuthorizer
    {
        public bool Allowed { get; set; } = true;
        public Task<bool> IsActiveOwnerAsync(string actorId, string organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(Allowed && actorId == "actor-1" && organizationId == "org-a");
    }

    private sealed class InMemorySessionStore : ICommunitySessionStore
    {
        private readonly Dictionary<Guid, DomainSessions.CommunitySession> _sessions = [];
        public int Count => _sessions.Count;
        public Task<DomainSessions.CommunitySession?> LoadAsync(Guid sessionId, CancellationToken ct) =>
            Task.FromResult(_sessions.GetValueOrDefault(sessionId));
        public Task<string?> GetOrganizationIdAsync(Guid sessionId, CancellationToken ct) =>
            Task.FromResult(_sessions.GetValueOrDefault(sessionId)?.OrganizationId);
        public Task InsertAsync(DomainSessions.CommunitySession session, string actorId, string reason, DateTimeOffset recordedAtUtc,
            SessionRevisionAction action, CancellationToken ct) { _sessions.Add(session.SessionId, session); return Task.CompletedTask; }
        public Task UpdateAsync(DomainSessions.CommunitySession session, long expectedVersion, string actorId, string reason,
            DateTimeOffset recordedAtUtc, SessionReminderSync reminderSync, IReadOnlyList<DomainSessions.SessionReminderOccurrence>? occurrences,
            SessionRevisionAction action, DateTimeOffset? previousStartUtc, CancellationToken ct, IReadOnlyList<SessionMaterialApproval>? materialApprovals = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<DomainSessions.CommunitySession>> ListPublicAsync(int take, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DomainSessions.CommunitySession>> QueryPublicAsync(PublicSessionQuery query, int take, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountRevisionsAsync(Guid sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionHistoryEntry>> ReadHistoryAsync(Guid sessionId, long? beforeVersion, int take, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountPendingRemindersAsync(Guid sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountSuppressedRemindersAsync(Guid sessionId, CancellationToken ct) => throw new NotSupportedException();
    }
}
