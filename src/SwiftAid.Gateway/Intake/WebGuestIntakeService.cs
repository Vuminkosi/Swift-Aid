using System.Security.Cryptography;
using SwiftAid.Application.Intake;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
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
public sealed class WebGuestIntakeService
{
    private readonly IServiceScopeFactory? _scopes;
    private readonly IIntakeSessionEndpointService? _sessions;
    private readonly IIntakeService? _intake;
    private readonly SemaphoreSlim _operation = new(1, 1);

    public WebGuestIntakeService(IServiceScopeFactory scopes) => _scopes = scopes;

    public WebGuestIntakeService(IIntakeSessionEndpointService sessions, IIntakeService intake)
    {
        _sessions = sessions;
        _intake = intake;
    }

    private async Task<T> InOperationAsync<T>(
        Func<IIntakeSessionEndpointService, IIntakeService, IServiceProvider?, Task<T>> action, CancellationToken ct)
    {
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_scopes is null) return await action(_sessions!, _intake!, null).ConfigureAwait(false);
            await using var scope = _scopes.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IIntakeSessionEndpointService>(),
                scope.ServiceProvider.GetRequiredService<IIntakeService>(), scope.ServiceProvider).ConfigureAwait(false);
        }
        finally { _operation.Release(); }
    }
    private Guid? _sessionId;
    private byte[]? _proof;
    private string? _opaqueCapability;

    public Task<IntakeReceipt> SubmitAsync(IntakeSubmission submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return InOperationAsync(async (sessions, intake, _) =>
        {
            var capability = await EnsureCapabilityAsync(sessions, ct).ConfigureAwait(false);
            try { return await intake.SubmitAsync(submission, capability, ct).ConfigureAwait(false); }
            catch (IntakeSubmissionException exception) when (exception.Code == IntakeErrorCode.CapabilityInvalid)
            {
                Reset();
                var renewed = await EnsureCapabilityAsync(sessions, ct).ConfigureAwait(false);
                return await intake.SubmitAsync(submission, renewed, ct).ConfigureAwait(false);
            }
        }, ct);
    }

    public Task<IntakeReceipt?> GetReceiptAsync(Guid receiptId, CancellationToken ct = default) =>
        InOperationAsync<IntakeReceipt?>(async (sessions, _, _) => _proof is null ? null :
            await sessions.GetReceiptAsync(receiptId, _proof, ct).ConfigureAwait(false), ct);

    public Task<EllaiGuidanceProgressDto?> CompleteGuidanceReceiptAsync(string ellaiSessionId,
        EllaiGuidanceReceiptRequest request, EllaiUiEntitlements authority,
        EllaiGuidanceProposalAdmission admission, CancellationToken ct) =>
        InOperationAsync<EllaiGuidanceProgressDto?>(async (sessions, _, provider) =>
        {
            if (_proof is null) return null;
            var proof = _proof.ToArray();
            try
            {
                var operationAdmission = provider?.GetRequiredService<EllaiGuidanceProposalAdmission>() ?? admission;
                return await operationAdmission.CompleteReceiptStepAsync(
                    ellaiSessionId, request, proof, authority, sessions, ct).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(proof); }
        }, ct);

    private async Task<IntakeCapability> EnsureCapabilityAsync(IIntakeSessionEndpointService sessions, CancellationToken ct)
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
