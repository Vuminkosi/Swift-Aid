using SwiftAid.UI.Components.Theme;

namespace SwiftAid.Mobile.Services.Theme;

public sealed class MobileThemeAppearanceSink : IThemeAppearanceSink
{
    public Task ApplyAsync(string preference, bool isDarkMode) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        if (Application.Current is { } application)
        {
            application.UserAppTheme = preference switch
            {
                "light" => AppTheme.Light,
                "dark" => AppTheme.Dark,
                _ => AppTheme.Unspecified
            };
        }

#if ANDROID
        var window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
        if (window is null)
        {
            return;
        }

        var systemBars = new AndroidX.Core.View.WindowInsetsControllerCompat(window, window.DecorView);
        systemBars.AppearanceLightStatusBars = !isDarkMode;
        systemBars.AppearanceLightNavigationBars = !isDarkMode;
#endif
    });
}
