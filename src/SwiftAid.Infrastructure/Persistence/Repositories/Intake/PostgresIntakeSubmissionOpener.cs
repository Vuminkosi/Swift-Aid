using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

/// <summary>Opens sealed intake submissions with the same Data Protection purpose that sealed them.</summary>
public sealed class PostgresIntakeSubmissionOpener : IIntakeSubmissionOpener
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector;

    public PostgresIntakeSubmissionOpener(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = (dataProtectionProvider ?? throw new ArgumentNullException(nameof(dataProtectionProvider)))
            .CreateProtector("SwiftAid.Intake.Report.v1");
    }

    public Task<IntakeSubmission> OpenAsync(byte[] protectedPayload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protectedPayload);
        ct.ThrowIfCancellationRequested();

        byte[] plaintext;
        try
        {
            plaintext = _protector.Unprotect(protectedPayload);
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            throw new IntakePromotionUnavailableException();
        }

        try
        {
            return Task.FromResult(
                JsonSerializer.Deserialize<IntakeSubmission>(plaintext, JsonOptions) ??
                throw new IntakePromotionUnavailableException());
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new IntakePromotionUnavailableException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
