using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using SwiftAid.Client.Presentation;
using SwiftAid.Gateway.Presentation;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Identity;

namespace SwiftAid.Ui.Tests;

public sealed class GatewayAccountPageTests
{
    [Fact]
    public async Task SignOutNavigatesToPublicHomeAfterConfirmation()
    {
        var navigation = new TestNavigationManager();
        var client = new GatewayAccountPresentationClient(
            new SignedInAuthStateProvider(),
            TestUsers.Manager(),
            new StubUserProfileService(new UserProfileData("Amara Dlamini", null, null, null, null)),
            Options.Create(new FederationOptions()),
            navigation,
            new ConfirmedSignOutJsRuntime());

        var result = await client.SignOutAsync();

        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        Assert.Equal("http://localhost/", navigation.Uri);
    }

    [Fact]
    public async Task AccountStateLoadsCurrentUserNameWhenIdentityClaimsOmitProfileDetails()
    {
        var client = new GatewayAccountPresentationClient(
            new SignedInAuthStateProvider(),
            TestUsers.Manager(),
            new StubUserProfileService(new UserProfileData("Amara Dlamini", null, null, null, null)),
            Options.Create(new FederationOptions()),
            new TestNavigationManager(),
            NoOpJsRuntime.Instance);

        var result = await client.GetStateAsync();

        Assert.True(result.Value?.IsSignedIn);
        Assert.Equal("Amara Dlamini", result.Value?.DisplayName);
    }

    [Fact]
    public async Task AccountStateFallsBackToCurrentIdentityEmailWhenProfileNameIsMissing()
    {
        var client = new GatewayAccountPresentationClient(
            new SignedInAuthStateProvider(),
            TestUsers.Manager(),
            new StubUserProfileService(new UserProfileData(null, null, null, null, null)),
            Options.Create(new FederationOptions()),
            new TestNavigationManager(),
            NoOpJsRuntime.Instance);

        var result = await client.GetStateAsync();

        Assert.True(result.Value?.IsSignedIn);
        Assert.Equal("staff@example.org", result.Value?.DisplayName);
    }

    [Fact]
    public async Task ChangeEmailPageRecoversFromClientFailure()
    {
        var markup = await RenderPageAsync<global::SwiftAid.Gateway.Components.Pages.ChangeEmail>(
            services => services.AddSingleton<IUserProfileClient>(new ThrowingProfileClient()));

        Assert.Contains("temporarily unavailable", markup, StringComparison.Ordinal);
        Assert.Contains("Retry", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrganizationsPageRecoversFromClientFailure()
    {
        var markup = await RenderPageAsync<global::SwiftAid.Gateway.Components.Pages.Organizations>(
            services => services.AddSingleton<IOrganizationMembershipClient>(new ThrowingMembershipClient()));

        Assert.Contains("temporarily unavailable", markup, StringComparison.Ordinal);
        Assert.Contains("Retry", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedeemMapsTimeoutToTimeoutCopy()
    {
        var client = new GatewayOrganizationMembershipClient(
            new SignedInAuthStateProvider(),
            TestUsers.Manager(),
            new NeverCalledMembershipService(),
            new TimeoutInvitationService());

        var result = await client.RedeemAsync("CODE-1");

        Assert.Equal(UiCapabilityResultCode.Retryable, result.Code);
        Assert.Contains("timed out", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueMapsTimeoutToTimeoutCopy()
    {
        var client = new GatewayOrganizationMembershipClient(
            new SignedInAuthStateProvider(),
            TestUsers.Manager(),
            new NeverCalledMembershipService(),
            new TimeoutIssuanceService());

        var result = await client.IssueAsync("org-1", 5, null);

        Assert.Equal(UiCapabilityResultCode.Retryable, result.Code);
        Assert.Contains("timed out", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueReturnsCodeOnSuccess()
    {
        var client = new GatewayOrganizationMembershipClient(
            new SignedInAuthStateProvider(),
            TestUsers.Manager(),
            new NeverCalledMembershipService(),
            new SuccessfulIssuanceService());

        var result = await client.IssueAsync("org-1", 5, null);

        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        Assert.Equal("NEW-CODE-1", result.Value!.Code);
    }

    [Fact]
    public async Task RedeemMapsUnreachableHostToUnreachableCopy()
    {
        var client = new GatewayOrganizationMembershipClient(
            new SignedInAuthStateProvider(),
            TestUsers.Manager(),
            new NeverCalledMembershipService(),
            new UnreachableInvitationService());

        var result = await client.RedeemAsync("CODE-1");

        Assert.Equal(UiCapabilityResultCode.Retryable, result.Code);
        Assert.Contains("could not be reached", result.Message, StringComparison.Ordinal);
    }

    private static async Task<string> RenderPageAsync<TComponent>(Action<IServiceCollection> configure)
        where TComponent : IComponent
    {
        await using var services = CreateServices(configure);
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<TComponent>(ParameterView.Empty);
            return component.ToHtmlString();
        });
    }

    private static ServiceProvider CreateServices(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime>(NoOpJsRuntime.Instance);
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        services.AddSingleton<IErrorBoundaryLogger>(NoOpErrorBoundaryLogger.Instance);
        configure(services);
        return services.BuildServiceProvider();
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, NavigationOptions options) =>
            Uri = ToAbsoluteUri(uri).ToString();
    }

    private sealed class NoOpJsRuntime : JSRuntime
    {
        public static readonly NoOpJsRuntime Instance = new();

        protected override void BeginInvokeJS(
            long taskId,
            string identifier,
            string? argsJson,
            JSCallResultType resultType,
            long targetInstanceId)
        {
        }

        protected override void EndInvokeDotNet(
            DotNetInvocationInfo callInfo,
            in DotNetInvocationResult result)
        {
        }
    }

    private sealed class ConfirmedSignOutJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "import" || typeof(TValue) != typeof(IJSObjectReference))
                throw new InvalidOperationException("Unexpected JavaScript call in sign-out test.");

            return ValueTask.FromResult((TValue)(object)new ConfirmedSignOutModule());
        }
    }

    private sealed class ConfirmedSignOutModule : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "signOut" || typeof(TValue) != typeof(bool))
                throw new InvalidOperationException("Unexpected module call in sign-out test.");

            return ValueTask.FromResult((TValue)(object)true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpErrorBoundaryLogger : IErrorBoundaryLogger
    {
        public static readonly NoOpErrorBoundaryLogger Instance = new();

        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }

    private static HtmlRenderer CreateRenderer(IServiceProvider services) =>
        new(services, services.GetRequiredService<ILoggerFactory>());

    private sealed class ThrowingProfileClient : IUserProfileClient
    {
        public Task<UiCapabilityResult<UserProfileState>> GetAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe failure");

        public Task<UiCapabilityResult<UserProfileState>> SaveAsync(
            UserProfileEdit edit, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe failure");

        public Task<UiCapabilityResult<UserProfileState>> UploadAvatarAsync(
            UserProfileAvatarUpload upload, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe failure");

        public Task<UiCapabilityResult<UserProfileState>> DeleteAvatarAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe failure");

        public Task<UiCapabilityResult<UserProfileState>> RequestEmailChangeAsync(
            string newEmail, string currentPassword, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe failure");

        public Task<UiCapabilityResult<UserProfileState>> ConfirmEmailChangeAsync(
            string code, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe failure");
    }

    private sealed class ThrowingMembershipClient : IOrganizationMembershipClient
    {
        public Task<UiCapabilityResult<IReadOnlyList<MembershipView>>> GetMineAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe failure");

        public Task<UiCapabilityResult<IReadOnlyList<OrganizationDirectoryOption>>> SearchAsync(
            string? query, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe failure");

        public Task<UiCapabilityResult<MembershipRequestView>> RequestAsync(
            string organizationId, string? reason, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe failure");

        public Task<UiCapabilityResult<IReadOnlyList<MembershipRequestView>>> GetMyRequestsAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe failure");

        public Task<UiCapabilityResult<InvitationRedeemView>> RedeemAsync(
            string code, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe failure");

        public Task<UiCapabilityResult<InvitationIssueView>> IssueAsync(
            string organizationId,
            int maxUses,
            DateTimeOffset? expiresAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe failure");

        public Task<UiCapabilityResult<OrganizationDirectoryOption>> CreateAsync(
            string organizationId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe failure");
    }

    private sealed class SignedInAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "actor-1")
                ], "probe"))));
    }

    private static class TestUsers
    {
        public static UserManager<SwiftAidUser> Manager() =>
            new StubUserManager(new StubUserStore());

        private sealed class StubUserManager(IUserStore<SwiftAidUser> store)
            : UserManager<SwiftAidUser>(
                store,
                Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
                new PasswordHasher<SwiftAidUser>(),
                [],
                [],
                new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(),
                null!,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<UserManager<SwiftAidUser>>.Instance)
        {
            public override Task<SwiftAidUser?> GetUserAsync(ClaimsPrincipal principal) =>
                Task.FromResult<SwiftAidUser?>(new SwiftAidUser
                {
                    Id = "actor-1",
                    UserName = "staff@example.org",
                    Email = "staff@example.org"
                });
        }

        private sealed class StubUserStore : IUserStore<SwiftAidUser>
        {
            public void Dispose()
            {
            }

            public Task<string> GetUserIdAsync(SwiftAidUser user, CancellationToken cancellationToken) =>
                Task.FromResult(user.Id);

            public Task<string?> GetUserNameAsync(SwiftAidUser user, CancellationToken cancellationToken) =>
                Task.FromResult(user.UserName);

            public Task SetUserNameAsync(SwiftAidUser user, string? userName, CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public Task<string?> GetNormalizedUserNameAsync(SwiftAidUser user, CancellationToken cancellationToken) =>
                Task.FromResult(user.NormalizedUserName);

            public Task SetNormalizedUserNameAsync(SwiftAidUser user, string? normalizedName, CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public Task<IdentityResult> CreateAsync(SwiftAidUser user, CancellationToken cancellationToken) =>
                Task.FromResult(IdentityResult.Success);

            public Task<IdentityResult> UpdateAsync(SwiftAidUser user, CancellationToken cancellationToken) =>
                Task.FromResult(IdentityResult.Success);

            public Task<IdentityResult> DeleteAsync(SwiftAidUser user, CancellationToken cancellationToken) =>
                Task.FromResult(IdentityResult.Success);

            public Task<SwiftAidUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
                Task.FromResult<SwiftAidUser?>(null);

            public Task<SwiftAidUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
                Task.FromResult<SwiftAidUser?>(null);
        }
    }

    private sealed class StubUserProfileService(UserProfileData? profile) : IUserProfileService
    {
        public Task<UserProfileData?> GetAsync(string userId, CancellationToken cancellationToken) =>
            Task.FromResult(profile);

        public Task<UserProfileSaveResult> SaveAsync(
            string userId,
            UserProfileData profile,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NeverCalledMembershipService : IOrganizationMembershipService
    {
        public Task<OrganizationCreateResult> CreateAsync(
            string actorId, string organizationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("probe failure");

        public Task<IReadOnlyList<ActorMembershipRecord>> GetMineAsync(string actorId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("probe failure");

        public Task<IReadOnlyList<MembershipDirectoryRecord>> SearchAsync(string? query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("probe failure");

        public Task<MembershipRequestSubmitResult> RequestAsync(
            string actorId, string organizationId, string? reason, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("probe failure");

        public Task<IReadOnlyList<MembershipRequestRecord>> GetMyRequestsAsync(string actorId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("probe failure");
    }

    private sealed class TimeoutInvitationService : IOrganizationInvitationService
    {
        public Task<InvitationRedeemResult> RedeemAsync(
            string actorId, string code, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("probe timeout");

        public Task<InvitationIssueResult> CreateAsync(
            string actorId,
            string organizationId,
            int maxUses,
            DateTimeOffset? expiresAtUtc,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("probe failure");
    }

    private sealed class UnreachableInvitationService : IOrganizationInvitationService
    {
        public Task<InvitationRedeemResult> RedeemAsync(
            string actorId, string code, CancellationToken cancellationToken) =>
            throw new System.Net.Http.HttpRequestException("probe unreachable");

        public Task<InvitationIssueResult> CreateAsync(
            string actorId,
            string organizationId,
            int maxUses,
            DateTimeOffset? expiresAtUtc,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("probe failure");
    }

    private sealed class TimeoutIssuanceService : IOrganizationInvitationService
    {
        public Task<InvitationRedeemResult> RedeemAsync(
            string actorId, string code, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("probe failure");

        public Task<InvitationIssueResult> CreateAsync(
            string actorId,
            string organizationId,
            int maxUses,
            DateTimeOffset? expiresAtUtc,
            CancellationToken cancellationToken) =>
            throw new TaskCanceledException("probe timeout");
    }

    private sealed class SuccessfulIssuanceService : IOrganizationInvitationService
    {
        public Task<InvitationRedeemResult> RedeemAsync(
            string actorId, string code, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("probe failure");

        public Task<InvitationIssueResult> CreateAsync(
            string actorId,
            string organizationId,
            int maxUses,
            DateTimeOffset? expiresAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(new InvitationIssueResult(
                InvitationIssueStatus.Issued, "NEW-CODE-1", Guid.NewGuid(), expiresAtUtc));
    }
}
