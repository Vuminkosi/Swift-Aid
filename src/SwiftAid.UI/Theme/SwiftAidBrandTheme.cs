using MudBlazor;

namespace SwiftAid.UI.Theme;

/// <summary>
/// Shared light and dark palettes for the Gateway and Mobile hosts.
/// </summary>
public static class SwiftAidBrandTheme
{
    // Icon ramp, pixel-sampled from swiftaid-icon.png (azure -> teal -> green):
    // upper blue #1778EC, top azure #2189F3, top-right cyan #23A4DA,
    // right teal #10A3A8, mid-right green #27A961, lower-right green #148E58,
    // lower-left deep sea navy #045676.
    public const string BrandBlue = "#1778EC";
    public const string BrandTeal = "#10A3A8";
    public const string BrandGreen = "#27A961";
    public const string BrandNavy = "#045676";
    public const string BrandCyan = "#23A4DA";

    public static MudTheme Create() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = BrandBlue,
            PrimaryContrastText = "#FFFFFF",
            Secondary = BrandNavy,
            SecondaryContrastText = "#FFFFFF",
            Tertiary = BrandGreen,
            TertiaryContrastText = "#062D29",
            Info = "#2189F3",
            InfoDarken = "#2189F3",
            InfoContrastText = "#FFFFFF",
            Success = "#148E58",
            SuccessContrastText = "#FFFFFF",
            Error = "#B4233A",
            ErrorContrastText = "#FFFFFF",
            Dark = BrandNavy,
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
            Primary = BrandCyan,
            PrimaryContrastText = "#062D29",
            Secondary = BrandBlue,
            SecondaryContrastText = "#FFFFFF",
            Tertiary = BrandGreen,
            TertiaryContrastText = "#062D29",
            Info = BrandBlue,
            InfoDarken = BrandCyan,
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
