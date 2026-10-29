using SwiftAid.Client.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Browser.Services;

/// <summary>Prevents browser guest-intake network calls when the app is not hosted over HTTPS.</summary>
public sealed class BrowserUnavailableGuestIntakeTransport : IGuestIntakeTransport
{
    public Task<IntakeSessionResponse> CreateSessionAsync(
        Guid sessionId,
        string receiptProof,
        CancellationToken cancellationToken) => Unavailable<IntakeSessionResponse>(cancellationToken);

    public Task<IntakeReceipt> SubmitAsync(
        IntakeSubmission submission,
        Guid sessionId,
        string intakeCapability,
        string receiptProof,
        CancellationToken cancellationToken) => Unavailable<IntakeReceipt>(cancellationToken);

    public Task<IntakeReceipt> GetReceiptAsync(
        Guid receiptId,
        string receiptProof,
        CancellationToken cancellationToken) => Unavailable<IntakeReceipt>(cancellationToken);

    public Task<PublicIntakeMetadata> GetMetadataAsync(
        CancellationToken cancellationToken) => Unavailable<PublicIntakeMetadata>(cancellationToken);

    public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(
        CancellationToken cancellationToken) => Unavailable<PublicIntakeCountryCatalogue>(cancellationToken);

    private static Task<T> Unavailable<T>(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<T>(new GuestIntakeTransportException());
    }
}
