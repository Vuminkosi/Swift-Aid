using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class StaffSessionEditorReadEndpointTests
{
    [Fact]
    public async Task RequiresAuthenticationAndForwardsOnlyValidatedPrincipalAndVersion()
    {
        var service = new Reader();
        await using var app = await StartAsync(service);
        using var http = app.GetTestClient();
        var id = Guid.NewGuid();
        var path = $"/api/v1/session-editor/capabilities?sessionId={id:D}&version=3&actorId=other";
        using var anonymous = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(service.Principal);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var response = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("actor-1", service.Principal?.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(id, service.SessionId);
        Assert.Equal(3, service.Version);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var invalid = await http.GetAsync($"/api/v1/session-editor/capabilities?sessionId={id:D}");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        service.Deny = true;
        using var denied = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        service.Current = false;
        using var stale = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
    }

    [Fact]
    public async Task MobileClientReadsTheAuthorizedCapabilityContract()
    {
        var service = new Reader();
        await using var app = await StartAsync(service);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        var id = Guid.NewGuid();
        var result = await new StaffSessionEditorHttpClient(http).GetManagementCapabilitiesAsync(id, 3);
        Assert.True(result.Succeeded);
        Assert.True(result.Value!.CanEdit);
        Assert.Equal(id, result.Value.SessionId);
        Assert.Equal("actor-1", service.Principal?.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task MobileReviewUsesVersionedGatewayCommandAndValidatedActor()
    {
        var service = new Commands();
        await using var app = await StartCommandsAsync(service);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        var id = Guid.NewGuid();
        var result = await new StaffSessionEditorHttpClient(http).ExecuteAsync(new ReviewSessionCommand(id, 3, true, "review", DateTimeOffset.UnixEpoch));
        Assert.True(result.Succeeded);
        Assert.Equal("actor-1", service.Actor);
        var command = Assert.IsType<ReviewSessionCommand>(service.Command);
        Assert.Equal(id, command.SessionId);
        Assert.Equal(3, command.ExpectedVersion);
        Assert.True(command.AcknowledgeSelfReview);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, command.RecordedAtUtc);
    }

    [Fact]
    public async Task MobileLifecycleCommandsUseTheGatewayMappings()
    {
        var service = new Commands();
        await using var app = await StartCommandsAsync(service);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        var client = new StaffSessionEditorHttpClient(http);
        var id = Guid.NewGuid();
        SessionCommand[] commands =
        [
            new UpdateSessionCommand(id, 3, "Title", "Summary", "Audience", [], "Presenter", "English",
                DateTimeOffset.UtcNow.AddDays(1), 60, null, [new("source", "v1")], SessionVisibility.Public,
                SessionChannelType.None, null, [], "update", DateTimeOffset.UnixEpoch),
            new ScheduleSessionCommand(id, 3, "schedule", DateTimeOffset.UnixEpoch),
            new CancelSessionCommand(id, 3, "venue unavailable", "cancel", DateTimeOffset.UnixEpoch),
            new CompleteSessionCommand(id, 3, "complete", DateTimeOffset.UnixEpoch)
        ];
        foreach (var command in commands)
        {
            var result = await client.ExecuteAsync(command);
            Assert.True(result.Succeeded);
            Assert.Equal(command.GetType(), service.Command?.GetType());
            Assert.Equal(id, service.Command?.SessionId);
            Assert.Equal(3, service.Command?.ExpectedVersion);
            Assert.Equal("actor-1", service.Actor);
            if (command is CancelSessionCommand)
                Assert.Equal("venue unavailable", Assert.IsType<CancelSessionCommand>(service.Command).CancellationReason);
            if (command is UpdateSessionCommand)
                Assert.Equal("source", Assert.Single(Assert.IsType<UpdateSessionCommand>(service.Command).Materials).MaterialId);
        }
    }

    [Fact]
    public async Task StaleSessionCannotExecuteVersionedCommand()
    {
        var service = new Commands();
        var access = new SyntheticWorkspaceAccessRequest { Current = false };
        await using var app = await StartCommandsAsync(service, access);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        var id = Guid.NewGuid();
        var result = await new StaffSessionEditorHttpClient(http).ExecuteAsync(new ReviewSessionCommand(id, 3, true, "review", DateTimeOffset.UtcNow));
        Assert.Equal(UiCapabilityResultCode.Denied, result.Code);
        Assert.Null(service.Command);
    }

    [Fact]
    public async Task AccountVerificationOutageDoesNotExecuteTheCommand()
    {
        var service = new Commands();
        await using var app = await StartCommandsAsync(service, new SyntheticWorkspaceAccessRequest { Unavailable = true });
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        var result = await new StaffSessionEditorHttpClient(http).ExecuteAsync(new ReviewSessionCommand(Guid.NewGuid(), 3, true, "review", DateTimeOffset.UtcNow));
        Assert.Equal(UiCapabilityResultCode.Unavailable, result.Code);
        Assert.Null(service.Command);
    }

    private static async Task<WebApplication> StartAsync(Reader service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
        }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(IdentityConstants.ApplicationScheme, _ => { })
          .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IStaffSessionEditorReadRequest>(service);
        builder.Services.AddSingleton<IWorkspaceAccessRequest>(service);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapStaffSessionEditorReadEndpoints();
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> StartCommandsAsync(Commands service, SyntheticWorkspaceAccessRequest? access = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
        }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(IdentityConstants.ApplicationScheme, _ => { })
          .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ICommunitySessionService>(service);
        builder.Services.AddSingleton<IWorkspaceAccessRequest>(access ?? new SyntheticWorkspaceAccessRequest());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCommunitySessionEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class Commands : ICommunitySessionService
    {
        public SessionCommand? Command { get; private set; }
        public string? Actor { get; private set; }
        public Task<SessionCommandResult> ExecuteAsync(SessionCommand command, string actorId, CancellationToken ct)
        {
            Command = command;
            Actor = actorId;
            return Task.FromResult(new SessionCommandResult(command.SessionId, command.ExpectedVersion + 1, "Reviewed"));
        }
        public Task<CommunitySessionView?> GetStaffAsync(Guid sessionId, string actorId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PublicSessionView>> ListPublicAsync(int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<PublicSessionPage> QueryPublicAsync(PublicSessionQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<PublicSessionView?> GetPublicAsync(Guid sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetIcsAsync(Guid sessionId, DateTimeOffset generatedAtUtc, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetInvitationTextAsync(Guid sessionId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Reader : IStaffSessionEditorReadRequest, IWorkspaceAccessRequest
    {
        public ClaimsPrincipal? Principal { get; private set; }
        public Guid? SessionId { get; private set; }
        public long? Version { get; private set; }
        public bool Deny { get; set; }
        public bool Current { get; set; } = true;
        public Task<UiCapabilityResult<WorkspaceAccessState>> GetAsync(ClaimsPrincipal principal, string? workspace, CancellationToken ct) =>
            Task.FromResult(new UiCapabilityResult<WorkspaceAccessState>(Current ? UiCapabilityResultCode.Success : UiCapabilityResultCode.Denied,
                Current ? new([new("workspace", "workspace")], "workspace") : null, Metadata()));
        public Task<UiCapabilityResult<StaffSessionManagementCapabilities>> CapabilitiesAsync(ClaimsPrincipal principal, Guid? sessionId, long? version, CancellationToken ct)
        {
            Principal = principal;
            SessionId = sessionId;
            Version = version;
            return Task.FromResult(new UiCapabilityResult<StaffSessionManagementCapabilities>(Deny ? UiCapabilityResultCode.Denied : UiCapabilityResultCode.Success,
                Deny ? null : new(false, true, false, false, false, false, sessionId, version, "Draft", true), Metadata()));
        }
        public Task<UiCapabilityResult<string>> ActorAsync(ClaimsPrincipal principal, CancellationToken ct) =>
            Task.FromResult(new UiCapabilityResult<string>(UiCapabilityResultCode.Success, "actor-1", Metadata()));
        public Task<UiCapabilityResult<CommunitySessionView>> SessionAsync(ClaimsPrincipal principal, Guid sessionId, CancellationToken ct) =>
            Task.FromResult(new UiCapabilityResult<CommunitySessionView>(UiCapabilityResultCode.Denied, null, Metadata()));
        private static UiCapabilityMetadata Metadata() => new("session-editor", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true);
    }
}
