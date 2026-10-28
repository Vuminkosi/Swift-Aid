namespace SwiftAid.UI.Components.Theme;

/// <summary>
/// Shared design tokens extracted from the three owner reference boards
/// (copies in docs/design/references/) and the product icon
/// (swiftaid-icon.png at the repo root; brand blues/greens below are
/// pixel-sampled from it). Single source of truth for hex values,
/// MudBlazor status mapping, typography, spacing, and shape. MudBlazor remains
/// the primary renderer; <see cref="SwiftAid.UI.Theme.SwiftAidBrandTheme"/>
/// owns the live MudPalette. This class only names tokens so components and
/// docs stay consistent with the references.
/// </summary>
public static class SwiftAidDesignTokens
{
    // Brand / primary actions (web header buttons, mobile primary buttons).
    public const string BrandBlue = "#1778EC";
    public const string BrandNavy = "#102A43";
    public const string BrandNavyDeep = "#0B1F33";
    public const string BrandNavyMuted = "#193B5A";
    public const string ActionTeal = "#045676";
    public const string DarkTeal = "#23A4DA";
    public const string DarkLink = "#23A4DA";

    // Status pills observed on the reference boards.
    public const string Escalated = "#B4233A";
    public const string EscalatedBackground = "#FDE8EA";
    public const string InProgress = "#1778EC";
    public const string InProgressBackground = "#E3EFFC";
    public const string FollowUpDue = "#B45309";
    public const string FollowUpBackground = "#FEF3C7";
    public const string OpenNew = "#148E58";
    public const string OpenBackground = "#E3F4E9";
    public const string Monitoring = "#B45309";
    public const string Assigned = "#1778EC";
    public const string PendingSync = "#B45309";
    public const string Resolved = "#148E58";

    // Chart series (Cases-by-Disease legends on reference boards).
    public const string SeriesCholera = "#1778EC";
    public const string SeriesMalaria = "#27A961";
    public const string SeriesMeasles = "#F59E0B";
    public const string SeriesAnimal = "#8B5CF6";
    public const string SeriesTb = "#EF4444";

    // Map cluster badges.
    public const string ClusterHigh = "#EF4444";
    public const string ClusterMedium = "#F59E0B";
    public const string ClusterLow = "#1778EC";

    // Surfaces.
    public const string PageBackgroundLight = "#F5FAF8";
    public const string PageBackgroundNeutral = "#F4F7FB";
    public const string CardSurfaceLight = "#FFFFFF";
    public const string InkPrimaryLight = "#123B39";
    public const string InkSecondaryLight = "#3E5E5A";

    // Typography scale (px) observed: small labels/captions, body, card titles, page headings.
    public const int FontCaption = 12;
    public const int FontBody = 14;
    public const int FontCardTitle = 16;
    public const int FontPageHeading = 20;
    public const int FontStatValue = 24;

    // Spacing scale (px): 4-pt grid used by cards, tiles, and form steps.
    public const int SpaceXs = 4;
    public const int SpaceSm = 8;
    public const int SpaceMd = 16;
    public const int SpaceLg = 24;
    public const int SpaceXl = 32;

    // Shape.
    public const int RadiusCard = 12;
    public const int RadiusPill = 999;
    public const int RadiusButton = 8;

    // Responsive layout breakpoints shared by compact web and mobile shells.
    public const int BreakpointMobile = 600;
    public const int BreakpointTablet = 960;
    public const int BreakpointDesktop = 1280;

    // Accessible status surfaces are paired with foreground colors.
    public const string StatusInformation = "#175CD3";
    public const string StatusInformationBackground = "#EFF8FF";
    public const string StatusWarning = "#93370D";
    public const string StatusWarningBackground = "#FFFAEB";
    public const string StatusError = "#B42318";
    public const string StatusErrorBackground = "#FEF3F2";
    public const string StatusSuccess = "#067647";
    public const string StatusSuccessBackground = "#ECFDF3";

    /// <summary>Maps a reference-board status label to a MudBlazor color.</summary>
    public static MudBlazor.Color ToMudColor(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "escalated" => MudBlazor.Color.Error,
        "in progress" or "inprogress" => MudBlazor.Color.Info,
        "follow-up due" or "follow-up" or "followup due" => MudBlazor.Color.Warning,
        "monitoring" or "under monitoring" => MudBlazor.Color.Warning,
        "new" or "open" or "assigned" => MudBlazor.Color.Success,
        "resolved" or "closed" => MudBlazor.Color.Success,
        "pending sync" or "pending" => MudBlazor.Color.Warning,
        _ => MudBlazor.Color.Default,
    };
}
