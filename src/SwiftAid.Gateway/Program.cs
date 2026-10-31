using MudBlazor.Services;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
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
using SwiftAid.Infrastructure.Persistence.Protection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMudServices();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("ellai-guest-write", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    options.AddPolicy("ellai-guest-read", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});

builder.Services.AddScoped<IPublicIntakeProcessingPolicyClient, GatewayPublicIntakeProcessingPolicyClient>();
builder.Services.AddScoped<IEducationClient, StaticEducationClient>();
builder.Services.AddScoped<ISubscriptionClient, UnavailableSubscriptionClient>();
builder.Services.AddScoped<IAnalyticsClient, AnalyticsGatewayClient>();
builder.Services.AddScoped<IDashboardOverviewClient, DashboardOverviewGatewayClient>();
builder.Services.AddScoped<ICaseDetailSummaryClient, CaseDetailSummaryGatewayClient>();
builder.Services.AddScoped<ICaseCorrectionReadClient, CaseCorrectionReadGatewayClient>();


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
GatewayAuthenticationRegistration.Configure(builder.Services, builder.Configuration);

builder.Services.AddSwiftAidInfrastructure(builder.Configuration, builder.Environment);
GatewayAuthenticationRegistration.ConfigurePersistentCookieDataProtection(builder.Services);
builder.Services.AddScoped<SwiftAid.Application.Authorization.IScopeService,
    SwiftAid.Application.Authorization.ScopeService>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiSessionScopeResolver>();
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
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiUtteranceSubmissionService>();
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
builder.Services.AddSingleton<SwiftAid.Application.Ellai.EllaiSessionEventJournal>();
builder.Services.AddSingleton<SwiftAid.Application.Ellai.EllaiSessionResponseStore>();
builder.Services.AddSingleton<SwiftAid.Gateway.Ellai.EllaiResponseSpeechCoordinator>();
builder.Services.AddSingleton<SwiftAid.Application.Ellai.EllaiActivityStatusCoordinator>();
builder.Services.AddSingleton<SwiftAid.Application.Ellai.EllaiUtteranceQueue>();
builder.Services.AddHostedService<SwiftAid.Gateway.Ellai.EllaiUtteranceWorker>();
builder.Services.AddSingleton<SwiftAid.Application.Ellai.IEllaiUiObservationStore, SwiftAid.Application.Ellai.InMemoryEllaiUiObservationStore>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiScenarioDirector>();
builder.Services.AddSingleton<SwiftAid.Application.Ellai.EllaiConsentService>();
var voiceTurnOptions = new SwiftAid.Application.Ellai.EllaiVoiceTurnOptions(
    SpeechStartConfirmationMs: builder.Configuration.GetValue(
        "Ellai:VoiceTurn:SpeechStartConfirmationMs", 120),
    EndOfTurnSilenceMs: builder.Configuration.GetValue(
        "Ellai:VoiceTurn:EndOfTurnSilenceMs", 450),
    BargeInConfirmationMs: builder.Configuration.GetValue(
        "Ellai:VoiceTurn:BargeInConfirmationMs", 180),
    MaximumTurnDurationMs: builder.Configuration.GetValue(
        "Ellai:VoiceTurn:MaximumTurnDurationMs", 480_000));
voiceTurnOptions.Validate();
builder.Services.AddSingleton(voiceTurnOptions);
builder.Services.AddSingleton<SwiftAid.Application.Ellai.EllaiVoiceTurnCoordinator>();
builder.Services.AddScoped<SwiftAid.Gateway.Ellai.EllaiVoiceTranscriptionService>();
builder.Services.AddSingleton<SwiftAid.Application.Ellai.IVoiceActivityDetector,
    SwiftAid.Gateway.Ellai.SileroOnnxVoiceActivityDetector>();
builder.Services.AddTransient<SwiftAid.Gateway.Ellai.SileroVoiceActivityAudioStream>();
builder.Services.AddScoped<SwiftAid.UI.Ellai.EllaiLauncher>();
builder.Services.AddScoped<SwiftAid.UI.Ellai.EllaiEventPoller>();
builder.Services.AddScoped<SwiftAid.Contracts.Ellai.IEllaiReviewClient, SwiftAid.Gateway.Ellai.EllaiReviewGatewayClient>();
builder.Services.AddScoped<SwiftAid.Gateway.Ellai.EllaiGuestSessionIdentity>();
builder.Services.AddHttpClient<SwiftAid.Gateway.Ellai.IEllaiSpeechSynthesisClient,
    SwiftAid.Gateway.Ellai.EllaiSpeechSynthesisClient>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiTopicService>();
builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiTrainingVerifier>();
{
    var ellai = SwiftAid.Intelligence.Meta.EllaiOptions.Bind(key => builder.Configuration[key]);
    builder.Services.AddSingleton(ellai);
    builder.Services.AddHttpClient<SwiftAid.Intelligence.Meta.EllaiReasoningAdapter>();
    builder.Services.AddHttpClient<SwiftAid.Intelligence.Meta.OllamaEllaiReasoningProvider>();
    builder.Services.AddHttpClient<SwiftAid.Intelligence.Meta.ServerlessEllaiReasoningProvider>();
    builder.Services.AddScoped<SwiftAid.Intelligence.Meta.EllaiReasoningRouter>();
    builder.Services.AddSingleton<SwiftAid.Application.Ellai.EllaiUsageLedger>();
    builder.Services.AddScoped<SwiftAid.Application.Ellai.IEllaiTurnCompletionClient, SwiftAid.Intelligence.Meta.EllaiProviderTurnClient>();
    if (ellai.Enabled)
    {
        var policyPath = SwiftAid.Application.Ellai.EllaiRuntimePolicy.ResolvePath(builder.Environment.ContentRootPath, ellai.PolicyPath);
        var runtimePolicy = SwiftAid.Application.Ellai.EllaiRuntimePolicy.Load(policyPath, ellai.PolicyVersion);
        builder.Services.AddSingleton(runtimePolicy);
        builder.Services.AddScoped(sp => new SwiftAid.Application.Ellai.EllaiPromptBuilder(runtimePolicy.Text, runtimePolicy.Version));
        builder.Services.AddScoped<SwiftAid.Application.Ellai.EllaiReasoningLoop>();
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
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && !context.Request.Path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddAdditionalAssemblies(typeof(SwiftAid.UI.Ellai.Pages.EllaiSettingsAbout).Assembly)
    .AddInteractiveServerRenderMode();

app.MapControllers();
app.MapPublicIntakeEndpoints();
app.MapIntakeDecisionEndpoints();
app.MapIntakeCoordinationEndpoints();
app.MapManagedCaseRegistrationEndpoints();
app.MapCaseTimelineEndpoints();
app.MapCaseDetailEndpoints();
app.MapCaseQueueEndpoints();
app.MapMapAggregateEndpoints();
app.MapHomeOverviewEndpoints();
app.MapMonitoringQueueEndpoints();
app.MapCaseCommandEndpoints();
app.MapCommunitySessionEndpoints();
app.MapPublicSessionEndpoints();
app.MapEllaiEndpoints();
app.MapEllaiGuestEndpoints();

app.MapSwiftAidIdentityEndpoints(
    app.Services.GetRequiredService<FederationOptions>(),
    app.Services.GetRequiredService<IdentityServerOptions>());

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }))
        .WithName("LiveHealth");

app.Run();

public static class GatewayAuthenticationRegistration
{
    public static void ConfigurePersistentCookieDataProtection(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(descriptor =>
                descriptor.ServiceType == typeof(ProtectedPayloadDataProtectionProvider)))
        {
            return;
        }

        ConfigureCookieProvider(services, IdentityConstants.ApplicationScheme);
        ConfigureCookieProvider(services, IdentityConstants.ExternalScheme);
        ConfigureCookieProvider(services, EllaiGuestEndpoints.AuthenticationScheme);
    }

    private static void ConfigureCookieProvider(IServiceCollection services, string scheme)
    {
        services.AddOptions<CookieAuthenticationOptions>(scheme)
            .Configure<ProtectedPayloadDataProtectionProvider>(
                (options, provider) => options.DataProtectionProvider = provider);
    }

    public static bool Configure(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var identityServerEnabled = configuration.GetValue<bool>("Identity:Enabled");
        var federationEnabled = configuration.GetValue<bool>("Federation:Enabled");
        var identityEnabled = identityServerEnabled || federationEnabled;

        var authentication = services.AddAuthentication();
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options =>
        {
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
        });
        if (!identityEnabled)
        {
            authentication = services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = DisabledIdentityAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = DisabledIdentityAuthenticationHandler.SchemeName;
                options.DefaultForbidScheme = DisabledIdentityAuthenticationHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, DisabledIdentityAuthenticationHandler>(
                DisabledIdentityAuthenticationHandler.SchemeName,
                _ => { });
        }

        authentication.AddCookie(EllaiGuestEndpoints.AuthenticationScheme, options =>
        {
            options.Cookie.Name = ".SwiftAid.Ellai.Guest";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = string.Equals(
                configuration["ASPNETCORE_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase)
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromHours(2);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("SwiftAidStaffApi", policy =>
            {
                if (identityServerEnabled)
                {
                    policy.AddAuthenticationSchemes(
                        IdentityConstants.ApplicationScheme,
                        OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                }
                else if (federationEnabled)
                {
                    // Federated browser sign-in produces the application
                    // cookie. OpenIddict validation is registered only when
                    // the local Identity server is enabled.
                    policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme);
                }
                else
                {
                    policy.AddAuthenticationSchemes(DisabledIdentityAuthenticationHandler.SchemeName);
                }

                policy.RequireAuthenticatedUser();
            });
        });

        return identityEnabled;
    }
}

public sealed class DisabledIdentityAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "SwiftAid.DisabledIdentity";

    public DisabledIdentityAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        var returnUrl = $"{Request.PathBase}{Request.Path}{Request.QueryString}";
        var signInLocation = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            "/sign-in",
            "returnUrl",
            returnUrl);
        Response.Redirect(signInLocation);
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
