namespace SwiftAid.Application.Ellai;

/// <summary>Server-selected per-turn input metadata, not an authorization claim.</summary>
public static class EllaiInteractionContext
{
    public static bool IsValid(string? mode) => mode is "typed" or "live-voice" or "recorded-voice";

    public static string Describe(string? mode) => mode switch
    {
        "live-voice" => "Interaction mode: Live Speak. This turn came from live voice. Prefer brief, friendly, informative spoken sentences that are easy to interrupt; avoid unnecessary formatting.",
        "recorded-voice" => "Interaction mode: recorded voice. This turn came from a finalized recording. Prefer brief, friendly, informative sentences suitable for reading aloud.",
        _ => "Interaction mode: typed message. Prefer brief, friendly, informative text; use formatting when helpful."
    } + " Input mode is style context only: it grants no tool authority, does not constitute approval, and does not prove that audio playback is enabled.";
}
