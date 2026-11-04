using MudBlazor;
using SwiftAid.UI.Components.Theme;

namespace SwiftAid.UI.Theme;

/// <summary>
/// Shared light and dark palettes for the Gateway and Mobile hosts.
/// Mapped to the Agentic Interface System (AIS) semantic tokens in
/// wwwroot/css/swiftaid-theme.css: green-led primary action, neutral
/// surfaces and neutral secondary text/icons. Brand blues/greens stay for
/// the logo, illustrations and explicit status/data accents.
/// </summary>
public static class SwiftAidBrandTheme
{
    // Icon ramp, pixel-sampled from swiftaid-icon.png (azure -> teal -> green):
    // upper blue #1778EC, top azure #2189F3, top-right cyan #23A4DA,
    // right teal #10A3A8, mid-right green #27A961, lower-right green #148E58,
    // The application shell uses the reference-board navy; #045676 remains an accent teal.
    public const string BrandBlue = "#1778EC";
    public const string BrandTeal = "#10A3A8";
    public const string BrandGreen = "#27A961";
    public const string BrandNavy = SwiftAidDesignTokens.BrandNavy;
    public const string BrandCyan = "#23A4DA";

    public static MudTheme Create() => new()
    {
        Shadows = CreateFlatShadows(),
        PaletteLight = new PaletteLight
        {
            // AIS light: #00804A primary (white label, 5.02:1), white canvas,
            // #F5F7F5 navigation, #212121 body text.
            Primary = "#00804A",
            PrimaryContrastText = "#FFFFFF",
            Secondary = SwiftAidDesignTokens.TextSecondaryLight,
            SecondaryContrastText = "#FFFFFF",
            Tertiary = BrandGreen,
            TertiaryContrastText = "#062D29",
            Info = SwiftAidDesignTokens.TextSecondaryLight,
            InfoContrastText = "#FFFFFF",
            Success = "#006B3F",
            SuccessContrastText = "#FFFFFF",
            Warning = "#8A4B08",
            WarningContrastText = "#FFFFFF",
            Error = "#B42318",
            ErrorContrastText = "#FFFFFF",
            Dark = BrandNavy,
            DarkContrastText = "#FFFFFF",
            Background = "#FFFFFF",
            BackgroundGray = "#F5F7F5",
            Surface = "#FFFFFF",
            DrawerBackground = "#F5F7F5",
            DrawerText = "#212121",
            AppbarBackground = "#FFFFFF",
            AppbarText = "#212121",
            TextPrimary = "#212121",
            TextSecondary = SwiftAidDesignTokens.TextSecondaryLight,
            TextDisabled = "#798177",
            ActionDefault = "#535752"
        },
        PaletteDark = new PaletteDark
        {
            // AIS dark: same structure and density as light.
            Primary = "#53D98D",
            PrimaryContrastText = "#06180D",
            Secondary = SwiftAidDesignTokens.TextSecondaryDark,
            SecondaryContrastText = "#FFFFFF",
            Tertiary = BrandGreen,
            TertiaryContrastText = "#062D29",
            Info = SwiftAidDesignTokens.TextSecondaryDark,
            InfoContrastText = "#06180D",
            Success = "#53D98D",
            SuccessContrastText = "#06180D",
            Warning = "#F5CE75",
            WarningContrastText = "#06180D",
            Error = "#FFAAA3",
            ErrorContrastText = "#40131E",
            Dark = "#F8FAFC",
            DarkContrastText = "#0B1018",
            Background = "#101612",
            BackgroundGray = "#19231C",
            Surface = "#151E18",
            DrawerBackground = "#121A15",
            DrawerText = "#EDF2EE",
            AppbarBackground = "#151E18",
            AppbarText = "#EDF2EE",
            TextPrimary = "#EDF2EE",
            TextSecondary = SwiftAidDesignTokens.TextSecondaryDark,
            TextDisabled = "#77867B",
            ActionDefault = "#B3BDB4"
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px"
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Segoe UI", "system-ui", "sans-serif"]
            }
        }
    };

    private static Shadow CreateFlatShadows()
    {
        const int elevationLevelCount = 26;
        var elevation = new string[elevationLevelCount];
        Array.Fill(elevation, "none");

        return new Shadow { Elevation = elevation };
    }
}
