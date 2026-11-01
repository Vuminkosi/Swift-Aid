namespace SwiftAid.Contracts.Ellai;

/// <summary>User-selected languages; never an authorization or capability grant.</summary>
public sealed record EllaiLanguagePreferences(string InputLanguage = "eng", string OutputLanguage = "eng")
{
    public static bool IsSupportedCode(string? language) => language is "eng" or "sna" or "ssw" or "nya" or "tso";
    public bool IsValid() => IsSupportedCode(InputLanguage) && IsSupportedCode(OutputLanguage);
}
