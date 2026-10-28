namespace SwiftAid.UI.Components.Theme;

/// <summary>
/// Shared design tokens extracted from the three owner reference boards
/// (copies in docs/design/references/). Single source of truth for hex values,
/// MudBlazor status mapping, typography, spacing, and shape. MudBlazor remains
/// the primary renderer; <see cref="SwiftAid.UI.Theme.SwiftAidBrandTheme"/>
/// owns the live MudPalette. This class only names tokens so components and
/// docs stay consistent with the references.
/// </summary>
public static class SwiftAidDesignTokens
{
    // Brand / primary actions (web header buttons, mobile primary buttons).
    public const string BrandBlue = "#126FE1";
    public const string ActionTeal = "#00695C";
    public const string DarkTeal = "#55D5B7";
    public const string DarkLink = "#80BFFF";

    // Status pills observed on the reference boards.
    public const string Escalated = "#B4233A";
    public const string EscalatedBackground = "#FDE8EA";
    public const string InProgress = "#075CB5";
    public const string InProgressBackground = "#E3EFFC";
    public const string FollowUpDue = "#B45309";
    public const string FollowUpBackground = "#FEF3C7";
    public const string OpenNew = "#176B43";
    public const string OpenBackground = "#E3F4E9";
    public const string Monitoring = "#B45309";
    public const string Assigned = "#075CB5";
    public const string PendingSync = "#B45309";
    public const string Resolved = "#176B43";

    // Chart series (Cases-by-Disease legends on reference boards).
    public const string SeriesCholera = "#126FE1";
    public const string SeriesMalaria = "#23AE70";
    public const string SeriesMeasles = "#F59E0B";
    public const string SeriesAnimal = "#8B5CF6";
    public const string SeriesTb = "#EF4444";

    // Map cluster badges.
    public const string ClusterHigh = "#EF4444";
    public const string ClusterMedium = "#F59E0B";
    public const string ClusterLow = "#126FE1";

    // Surfaces.
    public const string PageBackgroundLight = "#F5FAF8";
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
