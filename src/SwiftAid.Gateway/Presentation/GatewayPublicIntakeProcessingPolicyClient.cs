using Microsoft.Extensions.Options;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Infrastructure.Persistence.Repositories.Intake;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Exposes only the public processing reference/version used by the intake validator.</summary>
public sealed class GatewayPublicIntakeProcessingPolicyClient(
    IOptions<IntakeConsentOptions> options) : IPublicIntakeProcessingPolicyClient
{
    private readonly IntakeConsentOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    public Task<UiCapabilityResult<PublicReportProcessingPolicy>> GetPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSafeReference(_options.ProcessingPolicyReference) ||
            !IsSafeVersion(_options.ProcessingPolicyVersion))
        {
            return Task.FromResult(UiCapabilityResults.Unavailable<PublicReportProcessingPolicy>(
                "public-intake-processing-policy"));
        }

        var policy = new PublicReportProcessingPolicy(
            _options.ProcessingPolicyReference,
            _options.ProcessingPolicyVersion);
        var metadata = new UiCapabilityMetadata(
            "public-intake-processing-policy",
            UiCapabilityResults.ContractVersion,
            UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow,
            IsAvailable: true);
        return Task.FromResult(new UiCapabilityResult<PublicReportProcessingPolicy>(
            UiCapabilityResultCode.Success,
            policy,
            metadata));
    }

    private static bool IsSafeReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl) || value.Contains('\\'))
        {
            return false;
        }

        if (value.StartsWith("/", StringComparison.Ordinal))
        {
            return !value.StartsWith("//", StringComparison.Ordinal) &&
                !value.Contains("..", StringComparison.Ordinal);
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(uri.Host) &&
            string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool IsSafeVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
}
