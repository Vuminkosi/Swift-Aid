using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Infrastructure.Persistence.Protection;

namespace SwiftAid.Infrastructure.Configuration;

/// <summary>
/// Forces enabled command-processing services to resolve before the host starts accepting work.
/// Resolving these services configures the DbContext and validates the persistent key ring without
/// opening a database or provider connection.
/// </summary>
internal sealed class CaseCommandProcessingStartupValidator(IServiceScopeFactory scopeFactory)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var scope = scopeFactory.CreateAsyncScope();
        _ = scope.ServiceProvider.GetRequiredService<ICaseCommandProcessor>();
        var provider = scope.ServiceProvider.GetRequiredService<ProtectedPayloadDataProtectionProvider>();
        var keyRingSecurityValidator = scope.ServiceProvider.GetRequiredService<IKeyRingSecurityValidator>();
        var protectedPayloadOptions = scope.ServiceProvider.GetRequiredService<ProtectedPayloadOptions>();
        ValidateKeyRing(provider);
        keyRingSecurityValidator.Validate(protectedPayloadOptions.KeysPath);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void ValidateKeyRing(ProtectedPayloadDataProtectionProvider provider)
    {
        byte[] plaintext = [0x53, 0x77, 0x69, 0x66, 0x74, 0x41, 0x69, 0x64];
        byte[]? protectedBytes = null;
        byte[]? roundTrip = null;
        try
        {
            var protector = provider.CreateProtector("SwiftAid.StartupValidation.v1");
            protectedBytes = protector.Protect(plaintext);
            roundTrip = protector.Unprotect(protectedBytes);
            if (!CryptographicOperations.FixedTimeEquals(plaintext, roundTrip))
            {
                throw new ProtectedCaseEventPayloadException();
            }
        }
        catch (ProtectedCaseEventPayloadException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException or
                ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            throw new ProtectedCaseEventPayloadException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (roundTrip is not null)
            {
                CryptographicOperations.ZeroMemory(roundTrip);
            }
        }
    }
}
