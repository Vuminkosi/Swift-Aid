using SwiftAid.Client.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Mobile.Services.Intake;

/// <summary>Fail-closed transport used when the full profile has no Gateway address.</summary>
public sealed class UnavailableGuestIntakeTransport : IGuestIntakeTransport
{
    public Task<IntakeSessionResponse> CreateSessionAsync(Guid sessionId, string receiptProof, CancellationToken cancellationToken) =>
        Task.FromException<IntakeSessionResponse>(NotAvailable(cancellationToken));

    public Task<IntakeReceipt> SubmitAsync(
        IntakeSubmission submission,
        Guid sessionId,
        string intakeCapability,
        string receiptProof,
        CancellationToken cancellationToken) =>
        Task.FromException<IntakeReceipt>(NotAvailable(cancellationToken));

    public Task<IntakeReceipt> GetReceiptAsync(Guid receiptId, string receiptProof, CancellationToken cancellationToken) =>
        Task.FromException<IntakeReceipt>(NotAvailable(cancellationToken));

    public Task<PublicIntakeMetadata> GetMetadataAsync(CancellationToken cancellationToken) =>
        Task.FromException<PublicIntakeMetadata>(NotAvailable(cancellationToken));

    public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken cancellationToken) =>
        Task.FromException<PublicIntakeCountryCatalogue>(NotAvailable(cancellationToken));

    private static GuestIntakeTransportException NotAvailable(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new GuestIntakeTransportException(isRetryable: true);
    }
}
