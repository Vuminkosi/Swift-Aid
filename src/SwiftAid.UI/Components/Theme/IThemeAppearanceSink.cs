namespace SwiftAid.UI.Components.Theme;

/// <summary>Optional host hook for applying the web theme preference to native application chrome.</summary>
public interface IThemeAppearanceSink
{
    Task ApplyAsync(string preference, bool isDarkMode);
}
