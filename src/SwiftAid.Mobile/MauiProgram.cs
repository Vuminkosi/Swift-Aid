using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;
using Duende.IdentityModel.OidcClient.Browser;
using SwiftAid.Client.LocalStore;
using SwiftAid.Client.Authentication;
using SwiftAid.Client.Mobile;
using SwiftAid.Client.Notifications;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Client.Sessions;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Client.Intake;
using SwiftAid.Mobile.Services.Notifications;
using SwiftAid.Mobile.Services.Authentication;
using SwiftAid.Mobile.Services.Intake;
using SwiftAid.Mobile.Services.Audio;
using SwiftAid.Mobile.Services.Connectivity;
using SwiftAid.Mobile.Services.Account;
using SwiftAid.Mobile.Services.Presentation;
using SwiftAid.Mobile.Services.Welcome;
using Plugin.Firebase.CloudMessaging;
#if ANDROID
using Plugin.Firebase.Core.Platforms.Android;
#elif IOS
using Plugin.Firebase.Core.Platforms.iOS;
#endif

namespace SwiftAid.Mobile;

using MudBlazor.Services;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		using var appSettingsStream = typeof(MauiProgram).Assembly.GetManifestResourceStream(
			"SwiftAid.Mobile.appsettings.json")
			?? throw new InvalidOperationException("The packaged appsettings.json resource is missing.");
		builder.Configuration.AddJsonStream(appSettingsStream);
#if DEBUG
		// Development overrides (typically a host-loopback gateway). Release builds
		// ship only the production appsettings.json above.
		var devSettingsStream = typeof(MauiProgram).Assembly.GetManifestResourceStream(
			"SwiftAid.Mobile.appsettings.Development.json");
		if (devSettingsStream is not null)
		{
			using (devSettingsStream)
			{
				builder.Configuration.AddJsonStream(devSettingsStream);
			}
		}
#endif

		var guestDataDirectory = FileSystem.AppDataDirectory;

		builder
			.UseMauiApp<App>()
#if ANDROID
            .ConfigureLifecycleEvents(events => events.AddAndroid(android => android.OnCreate((activity, _) =>
            {
                CrossFirebase.Initialize(activity, () => Platform.CurrentActivity ??
                throw new InvalidOperationException("The current Android activity is unavailable."));
                MobileNotificationAndroidResources.EnsureGentleChannel();
            })))
#elif IOS
            .ConfigureLifecycleEvents(events => events.AddiOS(iOS => iOS.WillFinishLaunching((_, _) =>
            {
                CrossFirebase.Initialize();
                FirebaseCloudMessagingImplementation.Initialize();
                return false;
            })))
#endif
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		builder.Services.AddMudServices();
        Uri? gatewayBaseAddress = null;
        builder.Services.AddSingleton<IPublicIntakeProcessingPolicyClient, UnavailablePublicIntakeProcessingPolicyClient>();
        builder.Services.AddSingleton<IEducationClient, StaticEducationClient>();
        builder.Services.AddSingleton<ISubscriptionClient, UnavailableSubscriptionClient>();
        
        
        
        builder.Services.AddSingleton<IAiInvestigationClient, UnavailableInvestigationClient>();
        builder.Services.AddSingleton<IMobileHomeOverviewClient, HomeOverviewHttpClient>();
        builder.Services.AddTransient<SwiftAid.Client.Presentation.IMapAggregateClient>(sp => new SwiftAid.Client.Presentation.MapAggregateHttpClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("SwiftAid.NotificationApi")));
        builder.Services.AddSingleton<IOrganizationManagementClient, UnavailableOrganizationManagementClient>();
        builder.Services.AddSingleton<ICaseWorkspaceClient, MobileCaseWorkspaceClient>();
        builder.Services.AddSingleton<ICaseDiscussionClient, UnavailableCaseDiscussionClient>();
        builder.Services.AddSingleton<ICaseDiscussionCommandClient, UnavailableCaseDiscussionCommandClient>();
        builder.Services.AddSingleton<ICaseEvidenceClient, UnavailableCaseEvidenceClient>();
        builder.Services.AddSingleton<ICaseEvidenceUploadClient, UnavailableCaseEvidenceUploadClient>();
        builder.Services.AddSingleton<ICaseEvidenceContentClient, UnavailableCaseEvidenceContentClient>();
        builder.Services.AddSingleton<ICaseDetailSummaryClient, CaseDetailSummaryHttpClient>();
        builder.Services.AddSingleton<ICaseObservationClient, UnavailableCaseObservationClient>();
        builder.Services.AddTransient<IPublicCommunitySessionClient, HttpPublicCommunitySessionClient>();

        var configuredGatewayAddress = builder.Configuration["SwiftAid:GatewayBaseAddress"];
        var isAndroidEmulator = DeviceInfo.Current.Platform == DevicePlatform.Android;
        if (!isAndroidEmulator && DeviceInfo.Current.Platform == DevicePlatform.Android && MobileGatewayAddress.IsLoopbackConfigured(configuredGatewayAddress))
        {
                throw new InvalidOperationException(
                    "SwiftAid:GatewayBaseAddress points at the device loopback, which is unreachable from a physical Android device. Set a reachable gateway address (LAN hostname or IP).");
        }

        gatewayBaseAddress = MobileGatewayAddress.Resolve(
                configuredGatewayAddress,
                "SwiftAid:GatewayBaseAddress",
                rewriteLoopbackForAndroidEmulator: isAndroidEmulator);

        var notificationHttp = builder.Services.AddHttpClient("SwiftAid.NotificationApi", client =>
        {
                if (gatewayBaseAddress is not null)
                    client.BaseAddress = new NativeSessionHttpOptions(gatewayBaseAddress).GatewayBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(30);
        });
        notificationHttp.ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false });
        if (gatewayBaseAddress is not null)
        {
                builder.Services.AddSingleton(new NativeSessionHttpOptions(gatewayBaseAddress));
                builder.Services.AddTransient<NativeSessionBearerHandler>();
                notificationHttp.AddHttpMessageHandler(sp => new NativeSessionBearerHandler(
                    sp.GetRequiredService<INativeSessionReader>(),
                    sp.GetRequiredService<NativeSessionHttpOptions>(),
                    sp.GetRequiredService<NativeLoginClient>(),
                    sp.GetRequiredService<TimeProvider>()));

                builder.Services.AddHttpClient<IGuestIntakeTransport, GuestIntakeHttpClient>(client =>
                {
                    client.BaseAddress = gatewayBaseAddress;
                    client.Timeout = TimeSpan.FromSeconds(30);
                }).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false });
                builder.Services.AddHttpClient<IGeoLookupClient, HttpGeoLookupClient>(client =>
                {
                    client.BaseAddress = gatewayBaseAddress;
                    client.Timeout = TimeSpan.FromSeconds(30);
                }).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false });
                builder.Services.AddSingleton<IGuestSubmissionCoordinator, GuestSubmissionCoordinator>();
        }
        else
        {
                builder.Services.AddSingleton<IGuestIntakeTransport, UnavailableGuestIntakeTransport>();
                builder.Services.AddSingleton<IGeoLookupClient, UnavailableGeoLookupClient>();
                builder.Services.AddSingleton<IGuestSubmissionCoordinator, LocalOnlyGuestSubmissionCoordinator>();
        }

        builder.Services.AddSingleton<INotificationFeedClient>(sp => new NotificationFeedClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("SwiftAid.NotificationApi")));
        builder.Services.AddSingleton<INotificationPlatformSettings, MobileNotificationPlatformSettings>();
        builder.Services.AddSingleton<IConnectivity>(_ => Connectivity.Current);
        builder.Services.AddSingleton<IMobileConnectivityStatusService, MobileConnectivityStatusService>();
        builder.Services.AddTransient<SwiftAid.Contracts.Intake.IIntakeCoordinationClient>(sp => new SwiftAid.Client.Coordination.IntakeCoordinationHttpClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("SwiftAid.NotificationApi")));
        builder.Services.AddTransient<SwiftAid.Contracts.Cases.ICaseQueueClient>(sp => new SwiftAid.Client.Cases.CaseQueueHttpClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("SwiftAid.NotificationApi")));
        builder.Services.AddTransient<SwiftAid.Contracts.Cases.ICaseTimelineClient>(sp => new SwiftAid.Client.Cases.CaseTimelineHttpClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("SwiftAid.NotificationApi")));
        builder.Services.AddTransient<SwiftAid.Contracts.Cases.ICaseCommandClient>(sp => new SwiftAid.Client.Cases.CaseCommandHttpClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("SwiftAid.NotificationApi")));
        builder.Services.AddTransient<SwiftAid.Contracts.Cases.IMonitoringQueueClient>(sp => new SwiftAid.Client.Cases.MonitoringQueueHttpClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("SwiftAid.NotificationApi")));
        builder.Services.AddTransient<SwiftAid.Client.Presentation.IWorkManagementClient, SwiftAid.Client.Presentation.CommandBackedWorkManagementClient>();
        builder.Services.AddTransient<SwiftAid.Client.Presentation.IIntakeReviewClient>(sp => new SwiftAid.Client.Presentation.HttpIntakeReviewClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("SwiftAid.NotificationApi")));
        builder.Services.AddTransient<SwiftAid.Client.Sessions.CommunitySessionHttpClient>(sp => new SwiftAid.Client.Sessions.CommunitySessionHttpClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("SwiftAid.NotificationApi")));

        var nativeIdentity = builder.Configuration.GetSection("SwiftAid:NativeIdentity");
        var nativeAuthority = MobileGatewayAddress.Resolve(
                nativeIdentity["Authority"],
                "SwiftAid:NativeIdentity:Authority",
                rewriteLoopbackForAndroidEmulator: isAndroidEmulator)?.ToString() ?? string.Empty;
        var nativeLoginOptions = new NativeLoginOptions(
                nativeAuthority,
                nativeIdentity["ClientId"] ?? string.Empty,
                nativeIdentity["RedirectUri"] ?? string.Empty,
                nativeIdentity["PostLogoutRedirectUri"] ?? string.Empty);
        nativeLoginOptions.Validate();
        builder.Services.AddSingleton(nativeLoginOptions);
        builder.Services.AddSingleton<Duende.IdentityModel.OidcClient.Browser.IBrowser, MauiSystemBrowser>();
        builder.Services.AddSingleton<INativeOidcClientFactory, DuendeNativeOidcClientFactory>();
        builder.Services.AddSingleton<SecureNativeSessionStore>();
        builder.Services.AddSingleton<INativeSessionStore>(sp => sp.GetRequiredService<SecureNativeSessionStore>());
        builder.Services.AddSingleton<INativeSessionReader>(sp => sp.GetRequiredService<SecureNativeSessionStore>());
        builder.Services.AddSingleton<NativeLoginClient>();
        builder.Services.AddSingleton<MobileLogoutService>();
        builder.Services.AddSingleton<IMobileAuthenticationSession, MobileAuthenticationSession>();
        builder.Services.AddSingleton<IAccountPresentationClient, MobileAccountPresentationClient>();
        builder.Services.AddSingleton<IWorkspaceAccessClient, UnavailableWorkspaceAccessClient>();
        builder.Services.AddSingleton<IStaffSessionEditorClient, MobileUnavailableStaffSessionEditorClient>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IMobileWelcomePreferenceStore, MobileWelcomePreferenceStore>();
#if ANDROID
        builder.Services.AddSingleton<AndroidAudioCaptureService>();
        builder.Services.AddSingleton<IAudioCaptureService>(services => services.GetRequiredService<AndroidAudioCaptureService>());
        builder.Services.AddSingleton<IAudioPlaybackService>(services => services.GetRequiredService<AndroidAudioCaptureService>());
#else
        builder.Services.AddSingleton<UnsupportedMobileAudioCaptureService>();
        builder.Services.AddSingleton<IAudioCaptureService>(services => services.GetRequiredService<UnsupportedMobileAudioCaptureService>());
        builder.Services.AddSingleton<IAudioPlaybackService>(services => services.GetRequiredService<UnsupportedMobileAudioCaptureService>());
#endif
        builder.Services.AddSingleton<IGuestInstallKeyStore, GuestInstallKeyStore>();
        builder.Services.AddSingleton<IGuestSubmissionPersistence>(sp => new EncryptedGuestSubmissionStore(
            guestDataDirectory,
            sp.GetRequiredService<IGuestInstallKeyStore>()));
        builder.Services.AddSingleton<IGuestDraftMutationGate>(GuestDraftMutationGate.Instance);
        builder.Services.AddSingleton<IGuestAudioCleanupJournal>(sp => new EncryptedGuestAudioCleanupJournal(
            guestDataDirectory,
            sp.GetRequiredService<IGuestInstallKeyStore>()));
        builder.Services.AddSingleton<IGuestSubmissionStore>(sp => new GuestDraftStore(
            sp.GetRequiredService<IGuestSubmissionPersistence>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IGuestIntakeTransport>(),
            sp.GetRequiredService<IGuestAudioCleanupJournal>(),
            sp.GetRequiredService<IGuestAudioStore>(),
            sp.GetRequiredService<IGuestDraftMutationGate>()));
        builder.Services.AddSingleton<IGuestAudioStore>(sp => new EncryptedGuestAudioStore(
            guestDataDirectory,
            sp.GetRequiredService<IGuestInstallKeyStore>(),
            sp.GetRequiredService<IGuestSubmissionPersistence>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IGuestDraftMutationGate>()));

        var offlineDbFactory = new SwiftAidOfflineDbContextFactory(FileSystem.AppDataDirectory, gatewayBaseAddress);
        builder.Services.AddSingleton(offlineDbFactory);
        builder.Services.AddSingleton<IDbContextFactory<SwiftAidOfflineDbContext>>(offlineDbFactory);

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
