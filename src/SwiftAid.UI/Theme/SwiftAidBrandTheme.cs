using MudBlazor;

namespace SwiftAid.UI.Theme;

/// <summary>
/// Shared light and dark palettes for the Gateway and Mobile hosts.
/// </summary>
public static class SwiftAidBrandTheme
{
    public const string BrandBlue = "#126FE1";
    public const string BrandTeal = "#0B8B84";
    public const string BrandGreen = "#23AE70";

    public static MudTheme Create() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#00695C",
            PrimaryContrastText = "#FFFFFF",
            Secondary = BrandBlue,
            SecondaryContrastText = "#FFFFFF",
            Tertiary = BrandGreen,
            TertiaryContrastText = "#062D29",
            Info = "#075CB5",
            InfoDarken = "#075CB5",
            InfoContrastText = "#FFFFFF",
            Success = "#176B43",
            SuccessContrastText = "#FFFFFF",
            Error = "#B4233A",
            ErrorContrastText = "#FFFFFF",
            Dark = "#123B39",
            DarkContrastText = "#FFFFFF",
            Background = "#F5FAF8",
            BackgroundGray = "#E8F5F2",
            Surface = "#FFFFFF",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#123B39",
            AppbarBackground = "#FFFFFF",
            AppbarText = "#123B39",
            TextPrimary = "#123B39",
            TextSecondary = "#3E5E5A",
            TextDisabled = "#536E69",
            ActionDefault = "#3E5E5A"
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#55D5B7",
            PrimaryContrastText = "#062D29",
            Secondary = BrandBlue,
            SecondaryContrastText = "#FFFFFF",
            Tertiary = BrandGreen,
            TertiaryContrastText = "#062D29",
            Info = "#126FE1",
            InfoDarken = "#80BFFF",
            InfoContrastText = "#FFFFFF",
            Success = BrandGreen,
            SuccessContrastText = "#062D29",
            Error = "#FB7185",
            ErrorContrastText = "#40131E",
            Dark = "#F4FBF8",
            DarkContrastText = "#071B19",
            Background = "#071B19",
            BackgroundGray = "#1F4640",
            Surface = "#102B29",
            DrawerBackground = "#102B29",
            DrawerText = "#F4FBF8",
            AppbarBackground = "#102B29",
            AppbarText = "#F4FBF8",
            TextPrimary = "#F4FBF8",
            TextSecondary = "#C6DDD6",
            TextDisabled = "#A7C3BC",
            ActionDefault = "#C6DDD6"
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Segoe UI", "system-ui", "sans-serif"]
            }
        }
    };
}
