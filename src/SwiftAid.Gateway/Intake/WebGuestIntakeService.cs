using System.Security.Cryptography;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Gateway.Intake;

/// <summary>
/// Circuit-scoped anonymous web submit flow for the server-rendered report page.
/// Holds the intake session id, receipt proof, and opaque capability in server memory only,
/// which is the secure HttpOnly-session mapping from the P2.4 capability design: proof and
/// capability bytes never reach the browser, logs, or analytics. Sessions renew once on
/// expiry; anything else surfaces as an <see cref="IntakeSubmissionException"/> the page
/// turns into an honest message.
/// </summary>
public sealed class WebGuestIntakeService(
    IIntakeSessionEndpointService sessions,
    IIntakeService intake)
{
    private Guid? _sessionId;
    private byte[]? _proof;
    private string? _opaqueCapability;

    public async Task<IntakeReceipt> SubmitAsync(IntakeSubmission submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var capability = await EnsureCapabilityAsync(ct).ConfigureAwait(false);
        try
        {
            return await intake.SubmitAsync(submission, capability, ct).ConfigureAwait(false);
        }
        catch (IntakeSubmissionException exception) when (exception.Code == IntakeErrorCode.CapabilityInvalid)
        {
            Reset();
            var renewed = await EnsureCapabilityAsync(ct).ConfigureAwait(false);
            return await intake.SubmitAsync(submission, renewed, ct).ConfigureAwait(false);
        }
    }

    public async Task<IntakeReceipt?> GetReceiptAsync(Guid receiptId, CancellationToken ct = default)
    {
        if (_proof is null)
        {
            return null;
        }

        return await sessions.GetReceiptAsync(receiptId, _proof, ct).ConfigureAwait(false);
    }

    private async Task<IntakeCapability> EnsureCapabilityAsync(CancellationToken ct)
    {
        if (_sessionId is { } sessionId && _proof is { } proof && _opaqueCapability is { } opaque)
        {
            var existing = await sessions
                .ResolveCapabilityAsync(opaque, sessionId, proof, ct)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            Reset();
        }

        _sessionId = Guid.NewGuid();
        _proof = RandomNumberGenerator.GetBytes(32);
        var response = await sessions.CreateAsync(_sessionId.Value, _proof, ct).ConfigureAwait(false);
        if (response is null)
        {
            Reset();
            throw new IntakeSubmissionException(IntakeErrorCode.CapabilityInvalid);
        }

        _opaqueCapability = response.Capability;
        var capability = await sessions
            .ResolveCapabilityAsync(_opaqueCapability, _sessionId.Value, _proof, ct)
            .ConfigureAwait(false);
        if (capability is null)
        {
            Reset();
            throw new IntakeSubmissionException(IntakeErrorCode.CapabilityInvalid);
        }

        return capability;
    }

    private void Reset()
    {
        _sessionId = null;
        if (_proof is not null)
        {
            CryptographicOperations.ZeroMemory(_proof);
            _proof = null;
        }

        _opaqueCapability = null;
    }
}
