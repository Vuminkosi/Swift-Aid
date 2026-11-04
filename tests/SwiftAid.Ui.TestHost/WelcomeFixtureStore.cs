using SwiftAid.Mobile.Services.Welcome;

namespace SwiftAid.Ui.TestHost;

public sealed class WelcomeFixtureStore : IMobileWelcomePreferenceStore
{
    public event EventHandler<MobileWelcomePreferencesChangedEventArgs>? PreferencesChanged { add { } remove { } }
    public MobileWelcomePreferences? Get() => null;
    public void Save(MobileWelcomePreferences preferences) { }
}
