using System.Text.Json;
using Microsoft.Maui.Storage;
using SwiftAid.UI.Components.Surfaces.Public;

namespace SwiftAid.Mobile.Services.Welcome;

public sealed class MobileWelcomePreferenceStore : IMobileWelcomePreferenceStore
{
    private const string PreferenceKey = "swiftaid.mobile.welcome-selection.v1";
    private MobileWelcomePreferences? _sessionPreferences;

    public event EventHandler<MobileWelcomePreferencesChangedEventArgs>? PreferencesChanged;

    public MobileWelcomePreferences? Get()
    {
        if (_sessionPreferences is not null)
        {
            return _sessionPreferences;
        }

        var serialized = Preferences.Get(PreferenceKey, string.Empty);
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return null;
        }

        MobileWelcomePreferences? preferences;
        try
        {
            preferences = JsonSerializer.Deserialize<MobileWelcomePreferences>(serialized);
        }
        catch (JsonException)
        {
            Preferences.Remove(PreferenceKey);
            return null;
        }

        if (preferences is null || !Enum.IsDefined(preferences.SelectedIntent))
        {
            Preferences.Remove(PreferenceKey);
            return null;
        }

        _sessionPreferences = preferences with { CountryCode = NormalizeCountryCode(preferences.CountryCode) };
        return _sessionPreferences;
    }

    public void Save(MobileWelcomePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        if (!Enum.IsDefined(preferences.SelectedIntent) ||
            (!string.IsNullOrWhiteSpace(preferences.CountryCode) && NormalizeCountryCode(preferences.CountryCode) is null))
        {
            throw new ArgumentException("A supported role and optional country code are required.", nameof(preferences));
        }

        var normalized = preferences with { CountryCode = NormalizeCountryCode(preferences.CountryCode) };
        _sessionPreferences = normalized;

        var isPersisted = false;
        try
        {
            Preferences.Set(PreferenceKey, JsonSerializer.Serialize(normalized));
            isPersisted = true;
        }
        finally
        {
            PreferencesChanged?.Invoke(
                this,
                new MobileWelcomePreferencesChangedEventArgs(normalized, isPersisted));
        }
    }

    private static string? NormalizeCountryCode(string? countryCode) =>
        countryCode is { Length: 2 } && countryCode.All(char.IsAsciiLetter)
            ? countryCode.ToUpperInvariant()
            : null;
}
