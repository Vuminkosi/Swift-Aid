using SwiftAid.Client.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Intake;
using SwiftAid.Mobile.Services.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class MobilePublicIntakeMetadataClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountriesUseConfiguredTransportAndReportOutage(bool fail)
    {
        var client = new MobilePublicIntakeMetadataClient(new Transport(fail));
        var result = await client.GetCountriesAsync();
        Assert.Equal(fail ? UiCapabilityResultCode.Unavailable : UiCapabilityResultCode.Success, result.Code);
        if (!fail) Assert.Equal("SZ", Assert.Single(result.Value!.Countries).Code);
        else Assert.Null(result.Value);
    }

    private sealed class Transport(bool fail) : IGuestIntakeTransport
    {
        public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken cancellationToken) => fail
            ? Task.FromException<PublicIntakeCountryCatalogue>(new HttpRequestException())
            : Task.FromResult(new PublicIntakeCountryCatalogue("1", "/countries", [new("SZ", "Eswatini")]));
        public Task<PublicIntakeMetadata> GetMetadataAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IntakeSessionResponse> CreateSessionAsync(Guid sessionId, string receiptProof, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IntakeReceipt> SubmitAsync(IntakeSubmission submission, Guid sessionId, string capability, string receiptProof, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IntakeReceipt> GetReceiptAsync(Guid receiptId, string receiptProof, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
