using System.Security.Cryptography;
using SwiftAid.Application.Intake;
using SwiftAid.Client.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Gateway.Intake;

/// <summary>Uses the authoritative intake services directly from the interactive server.</summary>
public sealed class GatewayGuestIntakeTransport(
    IIntakeSessionEndpointService sessions,
    IIntakeService intake,
    IIntakePublicMetadataProvider metadata) : IGuestIntakeTransport
{
    public async Task<IntakeSessionResponse> CreateSessionAsync(
        Guid sessionId, string receiptProof, CancellationToken cancellationToken)
    {
        var proof = DecodeProof(receiptProof);
        try
        {
            var session = await sessions.CreateAsync(sessionId, proof, cancellationToken).ConfigureAwait(false);
            if (session is null || string.IsNullOrWhiteSpace(session.Capability) ||
                session.Capability.Length > 4096 || session.Capability.Any(char.IsWhiteSpace) ||
                session.Capability.Any(char.IsControl) || session.ExpiresAtUtc.Offset != TimeSpan.Zero)
                throw new GuestIntakeTransportException(isRetryable: false);
            return session;
        }
        finally { CryptographicOperations.ZeroMemory(proof); }
    }

    public async Task<IntakeReceipt> SubmitAsync(
        IntakeSubmission submission, Guid sessionId, string intakeCapability, string receiptProof,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var proof = DecodeProof(receiptProof);
        try
        {
            var capability = await sessions.ResolveCapabilityAsync(
                intakeCapability, sessionId, proof, cancellationToken).ConfigureAwait(false);
            if (capability is null || capability.SessionId != sessionId)
                throw new GuestIntakeTransportException(isRetryable: false);
            try
            {
                return await intake.SubmitAsync(submission, capability, cancellationToken).ConfigureAwait(false);
            }
            catch (IntakeSubmissionException exception)
            {
                throw new GuestIntakeTransportException(
                    isRetryable: exception.Code == IntakeErrorCode.RateLimited);
            }
        }
        finally { CryptographicOperations.ZeroMemory(proof); }
    }

    public async Task<IntakeReceipt> GetReceiptAsync(
        Guid receiptId, string receiptProof, CancellationToken cancellationToken)
    {
        var proof = DecodeProof(receiptProof);
        try
        {
            return await sessions.GetReceiptAsync(receiptId, proof, cancellationToken).ConfigureAwait(false)
                ?? throw new GuestIntakeTransportException(isRetryable: false);
        }
        finally { CryptographicOperations.ZeroMemory(proof); }
    }

    public Task<PublicIntakeMetadata> GetMetadataAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(metadata.GetMetadata());
    }

    public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken cancellationToken) =>
        metadata.GetCountriesAsync(cancellationToken);

    private static byte[] DecodeProof(string? encoded)
    {
        if (encoded is not { Length: 43 } || encoded.Contains('=') ||
            encoded.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new GuestIntakeTransportException(isRetryable: false);

        try
        {
            var proof = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + "=");
            var canonical = Convert.ToBase64String(proof).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (proof.Length == 32 && string.Equals(canonical, encoded, StringComparison.Ordinal)) return proof;
            CryptographicOperations.ZeroMemory(proof);
        }
        catch (FormatException) { }
        throw new GuestIntakeTransportException(isRetryable: false);
    }
}
