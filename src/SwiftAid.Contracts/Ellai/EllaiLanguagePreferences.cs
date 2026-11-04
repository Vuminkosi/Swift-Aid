namespace SwiftAid.Contracts.Ellai;

/// <summary>User-selected languages; never an authorization or capability grant.</summary>
public sealed record EllaiLanguagePreferences(string InputLanguage = "eng", string OutputLanguage = "eng",
    string EnglishVoiceId = "af_heart")
{
    public static bool IsSupportedCode(string? language) => language is "eng" or "sna" or "ssw" or "nya" or "tso";
    public static bool IsSupportedEnglishVoice(string? voiceId) => voiceId is "af_heart" or "am_michael";
    public bool IsValid() => IsSupportedCode(InputLanguage) && IsSupportedCode(OutputLanguage)
        && IsSupportedEnglishVoice(EnglishVoiceId);

    public Uri ApplyToVoiceUri(Uri uri)
    {
        if (!IsValid()) throw new ArgumentException("Invalid speech preferences.");
        var builder = new UriBuilder(uri);
        var query = string.Join('&', builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("outputLanguage=", StringComparison.OrdinalIgnoreCase)
                && !part.StartsWith("englishVoiceId=", StringComparison.OrdinalIgnoreCase)));
        builder.Query = (query.Length == 0 ? string.Empty : query + "&")
            + $"outputLanguage={Uri.EscapeDataString(OutputLanguage)}&englishVoiceId={Uri.EscapeDataString(EnglishVoiceId)}";
        return builder.Uri;
    }
}

public sealed record EllaiEnglishVoiceDto(string VoiceId, string Name, string Language = "eng");
