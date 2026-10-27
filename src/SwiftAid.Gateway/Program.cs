using SwiftAid.Gateway.Components;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Gateway.Notifications;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Identity;
using MudBlazor.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Notifications;
using SwiftAid.Client.Notifications;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddControllers();
builder.Services.AddMudServices();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization(options =>
{
    // Either the first-party cookie or a validated OIDC bearer authorizes staff API calls.
    options.AddPolicy("SwiftAidStaffApi", policy =>
    {
        policy.AddAuthenticationSchemes(
            IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
    });
});
builder.Services.AddSwiftAidInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddScoped<INotificationApiService, NotificationApiService>();
builder.Services.AddScoped<INotificationFeedClient, InteractiveServerNotificationFeedClient>();
builder.Services.AddScoped<INotificationPlatformSettings, GatewayNotificationPlatformSettings>();
builder.Services.AddSingleton<JetStreamNotificationChangeBridge>();
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<JetStreamNotificationChangeBridge>());
builder.Services.AddScoped<INotificationChangeSignal>(serviceProvider =>
{
    var natsOptions = serviceProvider.GetRequiredService<IOptions<NatsOptions>>().Value;
    return natsOptions.Enabled
        ? serviceProvider.GetRequiredService<JetStreamNotificationChangeBridge>()
        : ActivatorUtilities.CreateInstance<PollingNotificationChangeSignal>(serviceProvider);
});
builder.Services.AddHostedService<NotificationDeliveryWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapControllers();
app.MapPublicIntakeEndpoints();

app.MapSwiftAidIdentityEndpoints(
    app.Services.GetRequiredService<FederationOptions>(),
    app.Services.GetRequiredService<IdentityServerOptions>());

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }))
    .WithName("LiveHealth");

app.Run();
