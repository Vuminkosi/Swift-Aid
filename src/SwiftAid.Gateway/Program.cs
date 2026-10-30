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
using SwiftAid.Gateway.Cases;
using SwiftAid.Gateway.Components;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Gateway.Intake;
using SwiftAid.Gateway.Notifications;
using SwiftAid.Gateway.Presentation;
using SwiftAid.Gateway.Sessions;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence.Ellai;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMudServices();

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
builder.Services.AddScoped<IMapAggregateClient, MapAggregateGatewayClient>();
builder.Services.AddScoped<ICaseWorkspaceClient, CaseWorkspaceGatewayClient>();
builder.Services.AddScoped<IWorkManagementClient, CommandBackedWorkManagementClient>();
builder.Services.AddScoped<IIntakeReviewClient, GatewayIntakeReviewClient>();

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
Bit.Bonboarding.BitBonboarding.AddBitBonboardingServices(builder.Services, _ => { });
Bit.Bonboarding.BitBonboarding.AddBitBonboardingInMemoryStore(builder.Services);
builder.Services.AddEllaiPersistence();
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiSessionService>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiPolicyService>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiToolBroker>();
builder.Services.AddScoped(_ => new SwiftAid.Application.Ellai.EllaiTechnicalDocs(
    SwiftAid.Application.Ellai.EllaiTechnicalDocs.TryResolvePath(
        builder.Environment.ContentRootPath,
        builder.Configuration["Ellai:Docs:Path"] ?? "docs/TECHNICAL.md")));
builder.Services.AddSingleton<SwiftAid.Application.Ellai.EllaiUiRegistry>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiGuidancePlanValidator>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.IEllaiUiDiscoveryService, SwiftAid.Application.Ellai.EllaiUiDiscoveryService>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.IEllaiGuidancePlanner, SwiftAid.Application.Ellai.EllaiGuidancePlanner>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiGuidanceSessionCoordinator>();
builder.Services.AddScoped<SwiftAid.UI.Ellai.Guidance.IEllaiGuidanceControl, SwiftAid.Gateway.Ellai.EllaiGuidanceControlAdapter>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiCycleRunner>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiApprovedActionService>();

builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiApprovalService>();
builder.Services.AddSingleton<SwiftAid.Application.Ellai.EllaiApprovalDialogCoordinator>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiScenarioDirector>();
builder.Services.AddSingleton<SwiftAid.Application.Ellai.EllaiConsentService>();
builder.Services.AddScoped<SwiftAid.UI.Ellai.EllaiLauncher>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiTopicService>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiTrainingVerifier>();
{
    var ellai = SwiftAid.Intelligence.Meta.EllaiOptions.Bind(key => builder.Configuration[key]);
    builder.Services.AddSingleton(ellai);
    builder.Services.AddHttpClient<SwiftAid.Intelligence.Meta.EllaiReasoningAdapter>();
    builder.Services.AddHttpClient<SwiftAid.Intelligence.Meta.OllamaEllaiReasoningProvider>();
    builder.Services.AddHttpClient<SwiftAid.Intelligence.Meta.ServerlessEllaiReasoningProvider>();
    builder.Services.AddScoped<SwiftAid.Intelligence.Meta.EllaiReasoningRouter>();
    if (ellai.Enabled)
    {
        var policyPath = SwiftAid.Application.Ellai.EllaiRuntimePolicy.ResolvePath(builder.Environment.ContentRootPath, ellai.PolicyPath);
        var runtimePolicy = SwiftAid.Application.Ellai.EllaiRuntimePolicy.Load(policyPath, ellai.PolicyVersion);
        builder.Services.AddSingleton(runtimePolicy);
        builder.Services.AddScoped(sp => new SwiftAid.Application.Ellai.EllaiPromptBuilder(runtimePolicy.Text, runtimePolicy.Version));
    }

    if (ellai.HasUploadAsrRoute)
    {
        builder.Services.AddHttpClient<SwiftAid.Intelligence.Meta.MetaAsrUploadAdapter>();
    }

    if (ellai.HasRealtimeAsrRoute)
    {
        builder.Services.AddScoped<SwiftAid.Intelligence.Meta.MetaAsrRealtimeAdapter>();
    }

    var qdrantEndpoint = builder.Configuration["Qdrant:Endpoint"];
    var qdrantKey = builder.Configuration["Qdrant:ApiKey"];
    if (!string.IsNullOrWhiteSpace(qdrantEndpoint))
    {
        var endpoint = qdrantEndpoint;
        var key = qdrantKey ?? string.Empty;
        builder.Services.AddHttpClient<SwiftAid.Infrastructure.Memory.EllaiMemoryIndex>(
            (services, http) => new SwiftAid.Infrastructure.Memory.EllaiMemoryIndex(http, endpoint, key, "ellai-memory"));
    }
}
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

app.MapControllers();
app.MapPublicIntakeEndpoints();
app.MapIntakeDecisionEndpoints();
app.MapIntakeCoordinationEndpoints();
app.MapManagedCaseRegistrationEndpoints();
app.MapCaseTimelineEndpoints();
app.MapCaseQueueEndpoints();
app.MapMapAggregateEndpoints();
app.MapMonitoringQueueEndpoints();
app.MapCaseCommandEndpoints();
app.MapCommunitySessionEndpoints();
app.MapPublicSessionEndpoints();
app.MapEllaiEndpoints();

app.MapSwiftAidIdentityEndpoints(
    app.Services.GetRequiredService<FederationOptions>(),
    app.Services.GetRequiredService<IdentityServerOptions>());

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }))
        .WithName("LiveHealth");

app.Run();
