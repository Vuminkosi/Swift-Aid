using System.Net;
using System.Net.Http.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Mobile.Services.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class CaseCorrectionReadHttpClientTests
{
    [Fact]
    public void Snapshot_ValidatesCaseVersionsKindAndNarrativeCapability()
    {
        var id = Guid.NewGuid();
        Assert.True(new CaseDetailCorrectionSnapshot(id, 2, 1, CaseDetailCorrectionReportKind.LegacyV1, true, "original")
            .IsValidFor(id));
        Assert.False(new CaseDetailCorrectionSnapshot(id, 0, 1, CaseDetailCorrectionReportKind.LegacyV1, true, "original")
            .IsValidFor(id));
        Assert.False(new CaseDetailCorrectionSnapshot(id, 2, 1, CaseDetailCorrectionReportKind.HumanV2, true, "private")
            .IsValidFor(id));
        Assert.False(new CaseDetailCorrectionSnapshot(id, 2, 1, CaseDetailCorrectionReportKind.AnimalV2, false, "private")
            .IsValidFor(id));
        Assert.False(new CaseDetailCorrectionSnapshot(id, 2, 1, (CaseDetailCorrectionReportKind)99, false, null)
            .IsValidFor(id));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, UiCapabilityResultCode.Denied)]
    [InlineData(HttpStatusCode.NotFound, UiCapabilityResultCode.Denied)]
    [InlineData(HttpStatusCode.BadRequest, UiCapabilityResultCode.InvalidRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable, UiCapabilityResultCode.Retryable)]
    [InlineData(HttpStatusCode.InternalServerError, UiCapabilityResultCode.Retryable)]
    public async Task Get_MapsStatuses(HttpStatusCode status, UiCapabilityResultCode expected)
    {
        var client = Create(_ => Task.FromResult(new HttpResponseMessage(status)));
        var result = await client.GetAsync(Guid.NewGuid());
        Assert.Equal(expected, result.Code);
    }

    [Fact]
    public async Task Get_MapsTypedSuccessAndRejectsMalformedOrMismatchedBodies()
    {
        var caseId = Guid.NewGuid();
        var snapshot = new CaseDetailCorrectionSnapshot(caseId, 3, 2,
            CaseDetailCorrectionReportKind.HumanV2, false, null);
        var result = await Create(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(snapshot) })).GetAsync(caseId);
        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        Assert.Equal(CaseDetailCorrectionReportKind.HumanV2, result.Value!.ReportKind);
        var malformed = await Create(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{not json") })).GetAsync(caseId);
        Assert.Equal(UiCapabilityResultCode.Retryable, malformed.Code);
        var wrongCase = await Create(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(snapshot with { CaseId = Guid.NewGuid() }) })).GetAsync(caseId);
        Assert.Equal(UiCapabilityResultCode.Retryable, wrongCase.Code);
    }

    [Fact]
    public async Task Get_PropagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(_ => throw new OperationCanceledException())
            .GetAsync(Guid.NewGuid(), cts.Token));
    }

    private static CaseCorrectionReadHttpClient Create(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(new HttpClient(new Handler(send)) { BaseAddress = new Uri("https://gateway.test/") });

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }
}
