using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using Microsoft.Maui.Storage;
using Duende.IdentityModel.OidcClient.Browser;
using SwiftAid.Client.LocalStore;
using SwiftAid.Client.Authentication;
using SwiftAid.Client.Notifications;
using SwiftAid.Client.Intake;
using SwiftAid.Mobile.Services.Notifications;
using SwiftAid.Mobile.Services.Authentication;
using SwiftAid.Mobile.Services.Intake;
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
        builder.Services.AddSingleton(_ => CrossFirebaseCloudMessaging.Current);
        builder.Services.AddSingleton<INotificationPlatformSettings, MobileNotificationPlatformSettings>();
        builder.Services.AddSingleton<IGuestInstallKeyStore, GuestInstallKeyStore>();
        builder.Services.AddSingleton<IGuestSubmissionPersistence>(sp => new EncryptedGuestSubmissionStore(
            FileSystem.AppDataDirectory,
            sp.GetRequiredService<IGuestInstallKeyStore>()));
        builder.Services.AddSingleton<IGuestSubmissionStore, GuestDraftStore>();
        builder.Services.AddSingleton<IGuestAudioStore>(sp => new EncryptedGuestAudioStore(
            FileSystem.AppDataDirectory,
            sp.GetRequiredService<IGuestInstallKeyStore>(),
            sp.GetRequiredService<IGuestSubmissionPersistence>(),
            sp.GetRequiredService<TimeProvider>()));

		var configuredGatewayAddress = builder.Configuration["SwiftAid:GatewayBaseAddress"];
		Uri? gatewayBaseAddress = null;
		if (!string.IsNullOrWhiteSpace(configuredGatewayAddress))
		{
			if (!Uri.TryCreate(configuredGatewayAddress, UriKind.Absolute, out gatewayBaseAddress) ||
				gatewayBaseAddress.Scheme != Uri.UriSchemeHttps ||
				!string.IsNullOrEmpty(gatewayBaseAddress.UserInfo) ||
				!string.IsNullOrEmpty(gatewayBaseAddress.Query) ||
				!string.IsNullOrEmpty(gatewayBaseAddress.Fragment))
			{
				throw new InvalidOperationException("SwiftAid:GatewayBaseAddress must be an HTTPS URL without credentials, query, or fragment.");
			}
		}

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SecureNativeSessionStore>();
        builder.Services.AddSingleton<INativeSessionStore>(sp => sp.GetRequiredService<SecureNativeSessionStore>());
        builder.Services.AddSingleton<INativeSessionReader>(sp => sp.GetRequiredService<SecureNativeSessionStore>());

        var nativeIdentity = builder.Configuration.GetSection("SwiftAid:NativeIdentity");
        var nativeLoginOptions = new NativeLoginOptions(
            nativeIdentity["Authority"] ?? string.Empty,
            nativeIdentity["ClientId"] ?? string.Empty,
            nativeIdentity["RedirectUri"] ?? string.Empty,
            nativeIdentity["PostLogoutRedirectUri"] ?? string.Empty);
        nativeLoginOptions.Validate();
        builder.Services.AddSingleton(nativeLoginOptions);
        builder.Services.AddSingleton<Duende.IdentityModel.OidcClient.Browser.IBrowser, MauiSystemBrowser>();
        builder.Services.AddSingleton<INativeOidcClientFactory, DuendeNativeOidcClientFactory>();
        builder.Services.AddSingleton<NativeLoginClient>();
        builder.Services.AddSingleton<MobileLogoutService>();
        builder.Services.AddSingleton<IMobileAuthenticationSession, MobileAuthenticationSession>();

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

            var guestHttp = builder.Services.AddHttpClient<IGuestIntakeTransport, GuestIntakeHttpClient>(client =>
            {
                client.BaseAddress = gatewayBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(30);
            });
            guestHttp.ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false });
            builder.Services.AddSingleton<IGuestSubmissionCoordinator, GuestSubmissionCoordinator>();
        }
        builder.Services.AddTransient<INotificationFeedClient>(sp => new NotificationFeedClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("SwiftAid.NotificationApi")));
		var offlineDbFactory = new SwiftAidOfflineDbContextFactory(
			FileSystem.AppDataDirectory,
			gatewayBaseAddress);
		builder.Services.AddSingleton(offlineDbFactory);
		builder.Services.AddSingleton<IDbContextFactory<SwiftAidOfflineDbContext>>(offlineDbFactory);

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
