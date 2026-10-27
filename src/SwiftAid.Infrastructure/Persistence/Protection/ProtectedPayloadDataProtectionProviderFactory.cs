using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Infrastructure.Persistence.Protection;

/// <summary>Creates a persistent, explicitly configured Data Protection provider.</summary>
public static class ProtectedPayloadDataProtectionProviderFactory
{
    public static ProtectedPayloadDataProtectionProvider Create(
        ProtectedPayloadOptions options,
        IHostEnvironment hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        var validator = new ProtectedPayloadOptionsValidator(hostEnvironment);
        var validation = validator.Validate(Options.DefaultName, options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                nameof(ProtectedPayloadOptions),
                typeof(ProtectedPayloadOptions),
                validation.Failures);
        }

        if (!options.Enabled)
        {
            throw new ProtectedCaseEventPayloadException();
        }

        X509Certificate2? certificate = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(options.CertificatePath))
            {
                certificate = X509CertificateLoader.LoadPkcs12FromFile(
                    options.CertificatePath,
                    options.CertificatePassword.AsSpan(),
                    X509KeyStorageFlags.EphemeralKeySet);

                if (!certificate.HasPrivateKey ||
                    certificate.NotBefore.ToUniversalTime() > DateTimeOffset.UtcNow ||
                    certificate.NotAfter.ToUniversalTime() <= DateTimeOffset.UtcNow)
                {
                    throw new ProtectedCaseEventPayloadException();
                }
            }

            var keyDirectory = new DirectoryInfo(options.KeysPath);
            IDataProtectionProvider provider = certificate is null
                ? DataProtectionProvider.Create(
                    keyDirectory,
                    builder => builder.SetApplicationName(options.ApplicationName))
                : DataProtectionProvider.Create(
                    keyDirectory,
                    builder => builder.SetApplicationName(options.ApplicationName),
                    certificate);

            var configuredProvider = new ProtectedPayloadDataProtectionProvider(provider, certificate);
            certificate = null;
            return configuredProvider;
        }
        catch (ProtectedCaseEventPayloadException)
        {
            certificate?.Dispose();
            throw;
        }
        catch (Exception exception) when (
            exception is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            certificate?.Dispose();
            throw new ProtectedCaseEventPayloadException();
        }
    }

}

/// <summary>A validated Data Protection provider that retains its certificate for its lifetime.</summary>
public sealed class ProtectedPayloadDataProtectionProvider : IDataProtectionProvider, IDisposable
{
    private readonly IDataProtectionProvider _innerProvider;
    private readonly X509Certificate2? _certificate;
    private int _disposed;

    internal ProtectedPayloadDataProtectionProvider(
        IDataProtectionProvider innerProvider,
        X509Certificate2? certificate)
    {
        _innerProvider = innerProvider;
        _certificate = certificate;
    }

    public IDataProtector CreateProtector(string purpose)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _innerProvider.CreateProtector(purpose);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _certificate?.Dispose();
        }
    }
}
