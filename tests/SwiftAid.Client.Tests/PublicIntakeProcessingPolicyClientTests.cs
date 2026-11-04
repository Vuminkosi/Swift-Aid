using System.Net;
using System.Net.Http.Json;
using SwiftAid.Client.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Client.Tests;

public sealed class PublicIntakeProcessingPolicyClientTests
{
    [Fact]
    public async Task CurrentDescriptorDoesNotRequireAnOptionalNotice()
    {
        using var http = Create(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new PublicReportProcessingPolicy("/privacy#privacy-use-heading", "2026-11-01"))
        });
        var result = await new HttpPublicIntakeProcessingPolicyClient(http).GetPolicyAsync();
        Assert.True(result.Succeeded);
        Assert.Equal(UiCapabilityProvenance.LiveService, result.Metadata.Provenance);
        Assert.Null(result.Value!.NoticeText);
    }

    [Theory]
    [InlineData("//other.test/privacy", "v1")]
    [InlineData("http://other.test/privacy", "v1")]
    [InlineData("/../privacy", "v1")]
    [InlineData("/privacy", "bad version")]
    public async Task UnsafeDescriptorsFailClosed(string reference, string version)
    {
        using var http = Create(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new PublicReportProcessingPolicy(reference, version)) });
        Assert.False((await new HttpPublicIntakeProcessingPolicyClient(http).GetPolicyAsync()).Succeeded);
    }

    [Fact]
    public async Task UnavailableOrMalformedPolicyCannotEnableSubmission()
    {
        using var unavailable = Create(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.False((await new HttpPublicIntakeProcessingPolicyClient(unavailable).GetPolicyAsync()).Succeeded);
        using var malformed = Create(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-json") });
        Assert.False((await new HttpPublicIntakeProcessingPolicyClient(malformed).GetPolicyAsync()).Succeeded);
        using var oversized = Create(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new PublicReportProcessingPolicy("/privacy", "v1", new string('x', 17000))) });
        Assert.False((await new HttpPublicIntakeProcessingPolicyClient(oversized).GetPolicyAsync()).Succeeded);
    }

    [Fact]
    public void ConfirmationsMustMatchCurrentPolicyAndCountryCatalogue()
    {
        var confirmation = new IntakeConfirmations(true, "SZ", "countries-v1", true, true,
            "/privacy", "v1", null, null, null);
        Assert.True(GuestSubmissionConfirmationPolicy.ProcessingIsCurrent(confirmation, "/privacy", "v1"));
        Assert.False(GuestSubmissionConfirmationPolicy.ProcessingIsCurrent(confirmation, "/privacy", "v2"));
        Assert.False(GuestSubmissionConfirmationPolicy.ProcessingIsCurrent(confirmation, null, null));
        Assert.True(GuestSubmissionConfirmationPolicy.CountryIsCurrent(confirmation, "SZ", "countries-v1"));
        Assert.False(GuestSubmissionConfirmationPolicy.CountryIsCurrent(confirmation, "SZ", "countries-v2"));
        Assert.False(GuestSubmissionConfirmationPolicy.CountryIsCurrent(confirmation, "ZA", "countries-v1"));
    }

    private static HttpClient Create(HttpResponseMessage response) =>
        new(new StubHandler(response)) { BaseAddress = new Uri("https://gateway.test/") };

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
