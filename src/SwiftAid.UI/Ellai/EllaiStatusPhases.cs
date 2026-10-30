namespace SwiftAid.UI.Ellai;

/// <summary>Canonical composer phase labels (§7.20). The status line renders
/// the mapped label; unknown or empty phases render nothing rather than a
/// fabricated state.</summary>
public static class EllaiStatusPhases
{
    public const string Preparing = "Preparing";
    public const string Discovering = "Discovering";
    public const string Planning = "Planning";
    public const string NeedsInput = "NeedsInput";
    public const string Ready = "Ready";
    public const string Active = "Active";
    public const string Replanning = "Replanning";
    public const string Blocked = "Blocked";

    public static string? Label(string? phase) => phase switch
    {
        Preparing => Preparing,
        Discovering => Discovering,
        Planning => Planning,
        NeedsInput => NeedsInput,
        Ready => Ready,
        Active => Active,
        Replanning => Replanning,
        Blocked => Blocked,
        _ => null,
    };
}
