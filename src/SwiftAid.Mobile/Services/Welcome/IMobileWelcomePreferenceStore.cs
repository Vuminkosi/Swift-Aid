using SwiftAid.UI.Components.Surfaces.Public;

namespace SwiftAid.Mobile.Services.Welcome;

public sealed record MobileWelcomePreferences(
    WelcomeNavigationIntent SelectedIntent,
    string? CountryCode,
    bool IsPublicWelcomeCompleted = false);

public sealed class MobileWelcomePreferencesChangedEventArgs : EventArgs
{
    public MobileWelcomePreferencesChangedEventArgs(
        MobileWelcomePreferences preferences,
        bool isPersisted)
    {
        Preferences = preferences;
        IsPersisted = isPersisted;
    }

    public MobileWelcomePreferences Preferences { get; }

    public bool IsPersisted { get; }
}

public interface IMobileWelcomePreferenceStore
{
    event EventHandler<MobileWelcomePreferencesChangedEventArgs>? PreferencesChanged;

    MobileWelcomePreferences? Get();

    void Save(MobileWelcomePreferences preferences);
}
