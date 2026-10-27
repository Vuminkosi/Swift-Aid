namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

public sealed class IntakeConsentOptions
{
    public const string SectionName = "Intake:Consent";

    public string ProcessingPolicyReference { get; set; } = string.Empty;
    public string ProcessingPolicyVersion { get; set; } = string.Empty;
    public string RecordingPolicyReference { get; set; } = string.Empty;
    public string RecordingPolicyVersion { get; set; } = string.Empty;

    public bool IsValid => IsValidReference(ProcessingPolicyReference) && IsValidVersion(ProcessingPolicyVersion) &&
                           IsValidReference(RecordingPolicyReference) && IsValidVersion(RecordingPolicyVersion);

    private static bool IsValidReference(string value) =>
        value.Length is > 0 and <= 256 && Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var uri) &&
        (!uri.IsAbsoluteUri || uri.Scheme is "https");

    private static bool IsValidVersion(string value) =>
        value.Length is > 0 and <= 64 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
}
