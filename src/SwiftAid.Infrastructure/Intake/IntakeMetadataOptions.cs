namespace SwiftAid.Infrastructure.Intake;

/// <summary>Versioned public metadata references and the immutable country catalogue asset.</summary>
public sealed record IntakeMetadataOptions
{
    public const string SectionName = "Intake:Metadata";

    public string CatalogueVersion { get; init; } = "2026-10-27.1";
    public string CatalogueReference { get; init; } = "/public/v1/intake/metadata/countries";
    public string CatalogueSha256 { get; init; } = "10F08AC21834BB4ECA2BA77D38005D7191445D02E3A45921E625D6D6E0BB8570";
    public string PrivacyNoticeVersion { get; init; } = "2026-10-27.1";
    public string PrivacyNoticeReference { get; init; } = "/privacy";
    public string PrivacyNoticeSourceCommit { get; init; } = "a9bc0e6d1703dd47e8b0f2e3f78ff6f0e6e8f461";

    public void Validate()
    {
        if (!ValidVersion(CatalogueVersion) || !SafeReference(CatalogueReference) ||
            CatalogueSha256.Length != 64 || !CatalogueSha256.All(Uri.IsHexDigit) ||
            !ValidVersion(PrivacyNoticeVersion) || !SafeReference(PrivacyNoticeReference) ||
            PrivacyNoticeSourceCommit.Length is < 7 or > 64 || !PrivacyNoticeSourceCommit.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Intake metadata configuration is invalid.", nameof(IntakeMetadataOptions));
        }
    }

    private static bool ValidVersion(string value) =>
        value.Length is > 0 and <= 64 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool SafeReference(string value) =>
        value.StartsWith("/", StringComparison.Ordinal) && !value.StartsWith("//", StringComparison.Ordinal) &&
        !value.Contains('\\') && !value.Contains("..", StringComparison.Ordinal) &&
        Uri.TryCreate(value, UriKind.Relative, out _);
}
