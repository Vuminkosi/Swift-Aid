using Android.App;
using Android.Content.PM;
using Android.OS;
using Plugin.Firebase.CloudMessaging;

namespace SwiftAid.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    new[] { Android.Content.Intent.ActionView },
    Categories = new[] { Android.Content.Intent.CategoryDefault, Android.Content.Intent.CategoryBrowsable },
    DataScheme = "online.swiftaid.mobile",
    DataPath = "/oauth2redirect")]
[IntentFilter(
    new[] { Android.Content.Intent.ActionView },
    Categories = new[] { Android.Content.Intent.CategoryDefault, Android.Content.Intent.CategoryBrowsable },
    DataScheme = "online.swiftaid.mobile",
    DataPath = "/signout-callback")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        FirebaseCloudMessagingImplementation.OnNewIntent(Intent);
    }

    protected override void OnNewIntent(Android.Content.Intent? intent)
    {
        base.OnNewIntent(intent);
        FirebaseCloudMessagingImplementation.OnNewIntent(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        MobileNotificationForeground.IsForeground = true;
    }

    protected override void OnPause()
    {
        MobileNotificationForeground.IsForeground = false;
        var voiceCapture = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services
            .GetService<SwiftAid.UI.Ellai.IEllaiNativeVoiceCapture>();
        if (voiceCapture is not null)
        {
            _ = voiceCapture.SuspendAsync();
        }
        base.OnPause();
    }
}
