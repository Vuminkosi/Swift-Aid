using System.Net;
using System.Net.Http.Headers;
using SwiftAid.Client.Intake;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Client.Tests;

/// <summary>Pins the public intake wire contract: the service serializes
/// receipts with camel-case members and camel-case string enums, and the
/// guest transport must read that exact shape.</summary>
public sealed class GuestIntakeHttpClientReceiptTests
{
    [Fact]
    public async Task SubmitAsync_ParsesServiceShapedCamelCaseReceipt()
    {
        var clientSubmissionId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        var body = """{"receiptId":"__RECEIPT__","clientSubmissionId":"__CLIENT__","status":"receivedUnverified","routingStatus":"noServiceCoverage","receivedAtUtc":"2026-11-01T14:54:50.502566+00:00"}"""
            .Replace("__RECEIPT__", receiptId.ToString("D"))
            .Replace("__CLIENT__", clientSubmissionId.ToString("D"));
        using var response = new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent(body)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var httpClient = new HttpClient(new ImmediateResponseHandler(response))
        {
            BaseAddress = new Uri("https://gateway.test/")
        };
        var transport = new GuestIntakeHttpClient(httpClient);

        var receipt = await transport.SubmitAsync(
            ValidSubmission(clientSubmissionId),
            Guid.NewGuid(),
            "synthetic-capability",
            new string('a', 43),
            CancellationToken.None);

        Assert.Equal(receiptId, receipt.ReceiptId);
        Assert.Equal(clientSubmissionId, receipt.ClientSubmissionId);
        Assert.Equal(IntakeStatus.ReceivedUnverified, receipt.Status);
        Assert.Equal(IntakeRoutingStatus.NoServiceCoverage, receipt.RoutingStatus);
        Assert.Equal(TimeSpan.Zero, receipt.ReceivedAtUtc.Offset);
    }

    private static IntakeSubmission ValidSubmission(Guid clientSubmissionId) => new(
        1,
        clientSubmissionId,
        IntakeSubjectScope.Human,
        "Saw a sick goat near the river.",
        ["Coughing"],
        [],
        new ReportedTime(DateTimeOffset.UtcNow, null, null, null, "+02:00", null, ReportedTimePrecision.Approximate),
        new ReportedTime(null, null, null, null, null, null, ReportedTimePrecision.Unknown),
        new LocationContext(
            "ZA",
            new OptionalPlace(null, null, PresenceState.Unknown),
            new OptionalPlace(null, null, PresenceState.Unknown),
            new OptionalPlace(null, null, PresenceState.Unknown),
            null,
            null,
            null,
            LocationSource.Reporter,
            LocationPrecision.Approximate,
            "2026.10.27-1"),
        new IntakeConfirmations(
            true, "ZA", "2026.10.27-1", true, true,
            "https://swiftaid.example/privacy", "policy-v1", null, null, null));

    private sealed class ImmediateResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
