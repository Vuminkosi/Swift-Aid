using MudBlazor.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Intake;
using SwiftAid.Application.Notifications;
using SwiftAid.Client.Notifications;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Gateway.Components;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Gateway.Intake;
using SwiftAid.Gateway.Notifications;
using SwiftAid.Gateway.Presentation;
using SwiftAid.Gateway.Presentation.Standalone;
using SwiftAid.Gateway.Sessions;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Identity;

var builder = WebApplication.CreateBuilder(args);
var useFakeServices =
    (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Test")) &&
    builder.Configuration.GetValue<bool>("SwiftAid:UseFakeServices");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMudServices();

if (useFakeServices)
{
    builder.Services.AddScoped<IPublicIntakeProcessingPolicyClient, SamplePublicIntakeProcessingPolicyClient>();
    builder.Services.AddScoped<IEducationClient, FakeEducationClient>();
    builder.Services.AddScoped<ISubscriptionClient, FakeSubscriptionClient>();
    builder.Services.AddScoped<IAnalyticsClient, FakeAnalyticsClient>();
    builder.Services.AddScoped<IDashboardOverviewClient, FakeDashboardOverviewClient>();
    builder.Services.AddScoped<IMapDataClient, UnavailableMapDataClient>();
    builder.Services.AddScoped<IAiInvestigationClient, FakeInvestigationClient>();
    builder.Services.AddScoped<FakeOrganizationClient>();
    builder.Services.AddScoped<IOrganizationClient>(services => services.GetRequiredService<FakeOrganizationClient>());
    builder.Services.AddScoped<IOrganizationManagementClient>(services => services.GetRequiredService<FakeOrganizationClient>());
    builder.Services.AddScoped<ICommunitySessionService, FakeCommunitySessionClient>();
    builder.Services.AddScoped<IPublicCommunitySessionClient, FakePublicCommunitySessionClient>();
    builder.Services.AddScoped<IAccountPresentationClient, GatewayAccountPresentationClient>();
    builder.Services.AddScoped<IWorkspaceAccessClient, GatewayUnavailableWorkspaceAccessClient>();
    builder.Services.AddScoped<IStaffSessionEditorClient, FakeStaffSessionEditorClient>();
}
else
{
    builder.Services.AddScoped<IPublicIntakeProcessingPolicyClient, GatewayPublicIntakeProcessingPolicyClient>();
    builder.Services.AddScoped<IEducationClient, UnavailableEducationClient>();
    builder.Services.AddScoped<ISubscriptionClient, UnavailableSubscriptionClient>();
    builder.Services.AddScoped<IAnalyticsClient, UnavailableAnalyticsClient>();
    builder.Services.AddScoped<IDashboardOverviewClient, UnavailableDashboardOverviewClient>();
    builder.Services.AddScoped<IMapDataClient, UnavailableMapDataClient>();
    builder.Services.AddScoped<IOrganizationClient, UnavailableOrganizationClient>();
    builder.Services.AddScoped<IAiInvestigationClient, UnavailableInvestigationClient>();
    builder.Services.AddScoped<IOrganizationManagementClient, UnavailableOrganizationManagementClient>();
    builder.Services.AddScoped<IAccountPresentationClient, GatewayAccountPresentationClient>();
    builder.Services.AddScoped<IWorkspaceAccessClient, GatewayUnavailableWorkspaceAccessClient>();
    builder.Services.AddScoped<IStaffSessionEditorClient, GatewayStaffSessionEditorClient>();
}
if (useFakeServices)
{
    builder.Services.AddScoped<IMapAggregateClient, FakeMapAggregateClient>();
}
else
{
    builder.Services.AddScoped<IMapAggregateClient, UnavailableMapAggregateClient>();
}

if (useFakeServices)
{
    builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = StandaloneAuthenticationHandler.SchemeName;
            options.DefaultChallengeScheme = StandaloneAuthenticationHandler.SchemeName;
        })
        .AddScheme<AuthenticationSchemeOptions, StandaloneAuthenticationHandler>(
            StandaloneAuthenticationHandler.SchemeName,
            _ => { });
    builder.Services.AddAuthorization();

    builder.Services.AddScoped<FakeCaseStore>();
    builder.Services.AddScoped<FakeCaseDiscussionClient>();
    builder.Services.AddScoped<ICaseDiscussionClient>(services => services.GetRequiredService<FakeCaseDiscussionClient>());
    builder.Services.AddScoped<ICaseDiscussionCommandClient>(services => services.GetRequiredService<FakeCaseDiscussionClient>());
    builder.Services.AddScoped<FakeCaseEvidenceClient>();
    builder.Services.AddScoped<ICaseEvidenceClient>(services => services.GetRequiredService<FakeCaseEvidenceClient>());
    builder.Services.AddScoped<ICaseEvidenceUploadClient>(services => services.GetRequiredService<FakeCaseEvidenceClient>());
    builder.Services.AddScoped<ICaseEvidenceContentClient>(services => services.GetRequiredService<FakeCaseEvidenceClient>());
    builder.Services.AddScoped<ICaseDetailSummaryClient, FakeCaseDetailSummaryClient>();
    builder.Services.AddScoped<ICaseObservationClient, FakeCaseObservationClient>();
    builder.Services.AddScoped<SwiftAid.Contracts.Cases.ICaseQueueClient, FakeCaseQueueClient>();
    builder.Services.AddScoped<SwiftAid.Contracts.Cases.ICaseTimelineClient, FakeCaseTimelineClient>();
    builder.Services.AddScoped<SwiftAid.Contracts.Cases.ICaseCommandClient, FakeCaseCommandClient>();
    builder.Services.AddScoped<SwiftAid.Contracts.Cases.IMonitoringQueueClient, FakeMonitoringQueueClient>();
    builder.Services.AddScoped<SwiftAid.Contracts.Intake.IIntakeCoordinationClient, FakeIntakeCoordinationClient>();
    builder.Services.AddScoped<IWorkManagementClient, FakeWorkManagementClient>();
    builder.Services.AddScoped<IPublicIntakeSubmissionClient, FakePublicIntakeSubmissionClient>();
    builder.Services.AddScoped<IPublicIntakeMetadataClient, StandalonePublicIntakeMetadataClient>();







    builder.Services.AddScoped<INotificationFeedClient, StandaloneNotificationFeedClient>();
    builder.Services.AddScoped<INotificationPlatformSettings, StandaloneNotificationPlatformSettings>();
}
else
{
    builder.Services.AddControllers();
    builder.Services.AddAuthentication();
    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("SwiftAidStaffApi", policy =>
        {
            policy.AddAuthenticationSchemes(
                IdentityConstants.ApplicationScheme,
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
            policy.RequireAuthenticatedUser();
        });
    });

    builder.Services.AddSwiftAidInfrastructure(builder.Configuration, builder.Environment);
    builder.Services.AddScoped<WebGuestIntakeService>();
    builder.Services.AddScoped<IPublicIntakeSubmissionClient, WebPublicIntakeSubmissionClient>();
    builder.Services.AddScoped<IPublicIntakeMetadataClient, WebPublicIntakeMetadataClient>();
    builder.Services.AddScoped<IPublicCommunitySessionClient, WebPublicCommunitySessionClient>();
    builder.Services.AddScoped<SwiftAid.Contracts.Intake.IIntakeCoordinationClient, IntakeCoordinationGatewayClient>();
    builder.Services.AddScoped<SwiftAid.Contracts.Cases.ICaseTimelineClient, SwiftAid.Gateway.Cases.CaseTimelineGatewayClient>();
    builder.Services.AddScoped<SwiftAid.Contracts.Cases.ICaseQueueClient, SwiftAid.Gateway.Cases.CaseQueueGatewayClient>();
    builder.Services.AddScoped<SwiftAid.Contracts.Cases.IMonitoringQueueClient, SwiftAid.Gateway.Cases.MonitoringQueueGatewayClient>();
    builder.Services.AddScoped<SwiftAid.Contracts.Cases.ICaseCommandClient, SwiftAid.Gateway.Cases.CaseCommandGatewayClient>();
    builder.Services.AddScoped<SessionGatewayClient>();
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
}

var app = builder.Build();

app.UseExceptionHandler("/Error", createScopeForErrors: true);
if (!app.Environment.IsDevelopment())
{
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

if (!useFakeServices)
{
    app.MapControllers();
    app.MapPublicIntakeEndpoints();
    app.MapIntakeDecisionEndpoints();
    app.MapIntakeCoordinationEndpoints();
    app.MapManagedCaseRegistrationEndpoints();
    app.MapCaseTimelineEndpoints();
    app.MapCaseQueueEndpoints();
    app.MapMonitoringQueueEndpoints();
    app.MapCaseCommandEndpoints();
    app.MapCommunitySessionEndpoints();
    app.MapPublicSessionEndpoints();

    app.MapSwiftAidIdentityEndpoints(
        app.Services.GetRequiredService<FederationOptions>(),
        app.Services.GetRequiredService<IdentityServerOptions>());

    app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }))
        .WithName("LiveHealth");
}

app.Run();
