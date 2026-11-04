namespace SwiftAid.UI.Ellai;

/// <summary>Free-floating composer position in CSS pixels, relative to the
/// viewport origin. Movement is handle-initiated or keyboard-driven; the
/// host clamps every update with <see cref="Resolve"/> so the composer
/// can never leave the visible viewport.</summary>
public sealed record EllaiComposerPosition(double X, double Y)
{
    public static EllaiComposerPosition Default => new(0, 0);

    public const double MoveStep = 24;

    public static EllaiComposerPosition Resolve(
        EllaiComposerPosition requested,
        double viewportWidth,
        double viewportHeight,
        double composerWidth = 360,
        double composerHeight = 160)
    {
        var maxX = Math.Max(0, viewportWidth - composerWidth);
        var maxY = Math.Max(0, viewportHeight - composerHeight);
        return new EllaiComposerPosition(
            Math.Clamp(requested.X, 0, maxX),
            Math.Clamp(requested.Y, 0, maxY));
    }

    public EllaiComposerPosition Move(double deltaX, double deltaY) => new(X + deltaX, Y + deltaY);
}
