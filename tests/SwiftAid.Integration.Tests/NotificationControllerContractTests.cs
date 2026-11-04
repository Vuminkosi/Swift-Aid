using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Notifications;
using SwiftAid.Gateway.Controllers.Notifications;
using SwiftAid.Gateway.Notifications;

namespace SwiftAid.Integration.Tests;

public sealed class NotificationControllerContractTests
{
    private static readonly Guid NotificationId = Guid.Parse("7f20003c-7a7b-4f30-9c8a-1623f4dd4441");
    private static readonly Guid ResourceId = Guid.Parse("192c15ca-6af5-4f02-aeae-a207853fd1de");
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    [Fact]
    public async Task Feed_serializes_only_the_minimized_recipient_authorized_view()
    {
        var service = new StubNotificationApiService();
        await using var host = await NotificationHost.StartAsync(service);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notifications");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("actor-1", service.LastActor?.ActorId);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, root.GetProperty("unreadCount").GetInt32());
        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(
            ["id", "kind", "createdAtUtc", "readAtUtc", "dismissedAtUtc", "action", "opaqueResourceReference", "displayText"],
            item.EnumerateObject().Select(property => property.Name));
        Assert.Equal(NotificationId.ToString("D"), item.GetProperty("id").GetString());
        Assert.Equal("A case needs your attention.", item.GetProperty("displayText").GetString());
        Assert.Equal(ResourceId.ToString("D"), item.GetProperty("opaqueResourceReference").GetString());
        Assert.False(item.TryGetProperty("recipientActorId", out _));
        Assert.False(item.TryGetProperty("caseTitle", out _));
        Assert.False(item.TryGetProperty("narrative", out _));
        Assert.False(item.TryGetProperty("location", out _));
        Assert.False(item.TryGetProperty("mediaUrl", out _));
    }

    [Fact]
    public async Task Anonymous_feed_request_is_rejected_before_service_access()
    {
        var service = new StubNotificationApiService();
        await using var host = await NotificationHost.StartAsync(service);

        using var response = await host.Client.GetAsync("/api/v1/notifications");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, service.GetCalls);
    }

    private sealed class StubNotificationApiService : INotificationApiService
    {
        public int GetCalls { get; private set; }
        public ActorContext? LastActor { get; private set; }

        public ValueTask<NotificationFeedPage> GetAsync(
            ActorContext actor,
            int pageSize,
            string? continuationToken,
            CancellationToken cancellationToken)
        {
            GetCalls++;
            LastActor = actor;
            return ValueTask.FromResult(new NotificationFeedPage(
                [new NotificationItemView(
                    NotificationId,
                    NotificationKind.CaseNeedsAttention,
                    CreatedAtUtc,
                    null,
                    null,
                    NotificationAction.OpenAssignedWork,
                    ResourceId.ToString("D"))],
                null,
                1,
                1));
        }

        public ValueTask<NotificationMutationStatus> MarkReadAsync(
            ActorContext actor, Guid notificationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(NotificationMutationStatus.NotFoundOrNotAuthorized);

        public ValueTask<NotificationMutationStatus> DismissAsync(
            ActorContext actor, Guid notificationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(NotificationMutationStatus.NotFoundOrNotAuthorized);

        public ValueTask<PushDeviceRegistrationStatus> RegisterDeviceAsync(
            ActorContext actor, PushDeviceRegistrationRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(PushDeviceRegistrationStatus.Invalid);

        public ValueTask<PushDeviceRemovalStatus> RemoveDeviceAsync(
            ActorContext actor, Guid installationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(PushDeviceRemovalStatus.NotFoundOrNotAuthorized);
    }

    private sealed class StubNotificationChangeSignal : INotificationChangeSignal
    {
        public ValueTask<NotificationChangeWaitResult> WaitForChangeAsync(
            ActorContext actor, TimeSpan timeout, CancellationToken cancellationToken) =>
            ValueTask.FromResult(NotificationChangeWaitResult.Heartbeat);
    }

    private sealed class NotificationHost(WebApplication application, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public static async Task<NotificationHost> StartAsync(INotificationApiService service)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddControllers()
                .AddApplicationPart(typeof(NotificationsController).Assembly)
                .ConfigureApplicationPartManager(manager =>
                    manager.FeatureProviders.Add(new NotificationsOnlyControllerFeatureProvider()));
            builder.Services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                    options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    IdentityConstants.ApplicationScheme, _ => { })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy("SwiftAidStaffApi", policy =>
                policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme,
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme).RequireAuthenticatedUser()));
            builder.Services.AddSingleton<INotificationApiService>(service);
            builder.Services.AddSingleton<INotificationChangeSignal, StubNotificationChangeSignal>();

            var application = builder.Build();
            application.UseAuthentication();
            application.UseAuthorization();
            application.MapControllers();
            await application.StartAsync().ConfigureAwait(false);
            return new NotificationHost(application, application.GetTestClient());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.StopAsync().ConfigureAwait(false);
            await application.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class NotificationsOnlyControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var controller in feature.Controllers
                         .Where(controller => controller.AsType() != typeof(NotificationsController))
                         .ToArray())
            {
                feature.Controllers.Remove(controller);
            }
        }
    }
}
