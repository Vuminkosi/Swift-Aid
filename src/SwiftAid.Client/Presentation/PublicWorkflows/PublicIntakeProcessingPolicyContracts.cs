namespace SwiftAid.Client.Presentation.PublicWorkflows;

/// <summary>Public, non-secret descriptor for the current report-handling terms.</summary>
public sealed record PublicReportProcessingPolicy(
    string Reference,
    string Version,
    string? NoticeText = null,
    string? AcknowledgementLabel = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => IsSafeReference(Reference) && IsSafeVersion(Version) &&
        IsSafeText(NoticeText, 2000) && IsSafeText(AcknowledgementLabel, 240);

    private static bool IsSafeReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value != value.Trim() ||
            value.Any(char.IsControl) || value.Contains('\\')) return false;
        if (value.StartsWith("/", StringComparison.Ordinal))
            return !value.StartsWith("//", StringComparison.Ordinal) && !value.Contains("..", StringComparison.Ordinal);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool IsSafeVersion(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool IsSafeText(string? value, int maxLength) => value is null || value.Length <= maxLength &&
        value.All(character => !char.IsControl(character) || character is '\r' or '\n' or '\t');
}

public interface IPublicIntakeProcessingPolicyClient
{
    Task<UiCapabilityResult<PublicReportProcessingPolicy>> GetPolicyAsync(
        CancellationToken cancellationToken = default);
}

public sealed class UnavailablePublicIntakeProcessingPolicyClient : IPublicIntakeProcessingPolicyClient
{
    public Task<UiCapabilityResult<PublicReportProcessingPolicy>> GetPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<PublicReportProcessingPolicy>("public-intake-processing-policy"));
    }
}
