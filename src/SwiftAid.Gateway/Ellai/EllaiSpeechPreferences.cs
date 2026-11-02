using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Gateway.Ellai;

internal static class EllaiSpeechPreferences
{
    public static EllaiLanguagePreferences? Read(IQueryCollection query)
    {
        if (query["outputLanguage"].Count > 1 || query["englishVoiceId"].Count > 1) return null;
        var preferences = new EllaiLanguagePreferences("eng", query["outputLanguage"].FirstOrDefault() ?? "eng",
            query["englishVoiceId"].FirstOrDefault() ?? "af_heart");
        return preferences.IsValid() ? preferences : null;
    }
}
