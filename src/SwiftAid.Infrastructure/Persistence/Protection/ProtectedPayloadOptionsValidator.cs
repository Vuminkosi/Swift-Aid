using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Infrastructure.Persistence.Protection;

/// <summary>Validates key-ring configuration before a protected payload store is enabled.</summary>
public sealed class ProtectedPayloadOptionsValidator(IHostEnvironment hostEnvironment)
    : IValidateOptions<ProtectedPayloadOptions>
{
    public ValidateOptionsResult Validate(string? name, ProtectedPayloadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ApplicationName) ||
            !string.Equals(options.ApplicationName, options.ApplicationName.Trim(), StringComparison.Ordinal) ||
            options.ApplicationName.Length > 128)
        {
            failures.Add("DataProtection:ApplicationName must be a non-empty canonical name of at most 128 characters.");
        }

        if (!Path.IsPathFullyQualified(options.KeysPath) || !Directory.Exists(options.KeysPath))
        {
            failures.Add("DataProtection:KeysPath must identify an existing absolute persistent directory.");
        }

        if (options.MaximumPayloadBytes is <= 0 or > int.MaxValue)
        {
            failures.Add("DataProtection:MaximumPayloadBytes must be a positive value supported by the runtime.");
        }

        var hasCertificatePath = !string.IsNullOrWhiteSpace(options.CertificatePath);
        var hasCertificatePassword = !string.IsNullOrEmpty(options.CertificatePassword);
        if (hasCertificatePath != hasCertificatePassword)
        {
            failures.Add("DataProtection certificate path and password must be configured together.");
        }

        var isDevelopment = string.Equals(
            hostEnvironment.EnvironmentName,
            Environments.Development,
            StringComparison.OrdinalIgnoreCase);

        if (options.AllowUnencryptedDevelopmentKeys && !isDevelopment)
        {
            failures.Add("DataProtection:AllowUnencryptedDevelopmentKeys is permitted only in Development.");
        }

        if (hasCertificatePath)
        {
            if (!Path.IsPathFullyQualified(options.CertificatePath) || !File.Exists(options.CertificatePath))
            {
                failures.Add("DataProtection:CertificatePath must identify an existing absolute certificate file.");
            }
        }
        else if (!isDevelopment || !options.AllowUnencryptedDevelopmentKeys)
        {
            failures.Add("DataProtection requires a certificate-protected key ring outside explicit Development mode.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
