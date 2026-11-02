using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Sessions;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class EventSubscriptionEndpointTests
{
    private static readonly Guid SessionId = Guid.Parse("d0c00001-2222-4a44-9aa9-123456789abc");

    [Fact]
    public async Task AnonymousRead_IsUnauthorizedAndDoesNotReachService()
    {
        await using var host = await HostFixture.StartAsync();

        using var response = await host.Client.GetAsync($"/api/v1/sessions/{SessionId:D}/subscription");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(host.Service.LastAccountId);
    }

    [Fact]
    public async Task AuthenticatedRead_UsesPrincipalAccountAndIgnoresCallerAccountQuery()
    {
        await using var host = await HostFixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/sessions/{SessionId:D}/subscription?accountId=another-user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("actor-1", host.Service.LastAccountId);
    }

    [Fact]
    public async Task MySubscriptions_ReturnsOnlyPrincipalAccount()
    {
        await using var host = await HostFixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/my/session-subscriptions?accountId=another-user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);

        using var response = await host.Client.SendAsync(request);
        var items = await response.Content.ReadFromJsonAsync<EventSubscription[]>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("actor-1", host.Service.LastAccountId);
        Assert.Equal("actor-1", Assert.Single(items!).AccountId);
    }

    [Fact]
    public async Task AuthenticatedMutation_WithoutAntiforgeryProof_IsRejectedBeforeService()
    {
        await using var host = await HostFixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{SessionId:D}/subscription")
        {
            Content = JsonContent.Create(new { sessionVersion = 1, consentReference = "consent", channel = "Email" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(host.Service.LastAccountId);
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly WebApplication _application;
        public HttpClient Client { get; }
        public FakeSubscriptionService Service { get; }

        private HostFixture(WebApplication application, HttpClient client, FakeSubscriptionService service)
        {
            _application = application;
            Client = client;
            Service = service;
        }

        public static async Task<HostFixture> StartAsync()
        {
            var service = new FakeSubscriptionService();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddAntiforgery();
            builder.Services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                    options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(IdentityConstants.ApplicationScheme, _ => { })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton<IEventSubscriptionService>(service);
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapEventSubscriptionEndpoints();
            await app.StartAsync();
            return new HostFixture(app, app.GetTestClient(), service);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }

    private sealed class FakeSubscriptionService : IEventSubscriptionService
    {
        public string? LastAccountId { get; private set; }

        public Task<EventSubscriptionOptions?> GetOptionsAsync(Guid sessionId, string accountId, CancellationToken cancellationToken)
        {
            LastAccountId = accountId;
            return Task.FromResult<EventSubscriptionOptions?>(new(sessionId, 3,
                $"session-reminder-consent-v1:{sessionId:N}:3", "consent", ["Email"], true));
        }

        public Task<EventSubscription?> GetAsync(Guid sessionId, string accountId, CancellationToken cancellationToken)
        {
            LastAccountId = accountId;
            return Task.FromResult<EventSubscription?>(new(sessionId, accountId, EventSubscriptionStatus.Unsubscribed, [], null, 0));
        }

        public Task<IReadOnlyList<EventSubscription>> ListMineAsync(string accountId, CancellationToken cancellationToken)
        {
            LastAccountId = accountId;
            return Task.FromResult<IReadOnlyList<EventSubscription>>(
                [new(SessionId, accountId, EventSubscriptionStatus.Active, ["Email"], "consent-reference", 2)]);
        }

        public Task<EventSubscription> SubscribeAsync(Guid sessionId, string accountId, long expectedSessionVersion,
            string consentReference, string channel, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EventSubscription> UnsubscribeAsync(Guid sessionId, string accountId, long expectedSubscriptionVersion,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> CanDispatchReminderAsync(Guid sessionId, string accountId, long sessionVersion,
            string consentReference, string channel, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
