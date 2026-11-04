using System.Net;
using System.Net.Http.Json;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiGuidanceAdmissionClientTests
{
    [Theory]
    [InlineData(true, "/api/v1/ellai/guest/sessions/session/guidance/admission")]
    [InlineData(false, "/api/v1/ellai/sessions/session/guidance/admission")]
    public async Task UsesCurrentSessionRouteAndEncodesProposalIdentity(bool guest, string path)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(path, request.RequestUri!.AbsolutePath);
            Assert.Equal("?proposalId=session%3Aturn%26other%3Dvalue%3A0", request.RequestUri.Query);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new EllaiGuidanceAdmissionDto("Ready", "Create a report", 3)) };
        })) { BaseAddress = new Uri("https://localhost/") };
        var result = await new EllaiReviewHttpClient(http, guest).ReadGuidanceAdmissionAsync("session", "session:turn&other=value:0", default);
        Assert.Equal("Ready", result!.Status);
        Assert.Equal(3, result.InteractionEpoch);
    }

    [Fact]
    public async Task DenialDoesNotBecomeReady()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound))) { BaseAddress = new Uri("https://localhost/") };
        Assert.Null(await new EllaiReviewHttpClient(http).ReadGuidanceAdmissionAsync("session", "unknown", default));
    }

    [Theory]
    [InlineData(true, "/api/v1/ellai/guest/sessions/session/guidance/plan")]
    [InlineData(false, "/api/v1/ellai/sessions/session/guidance/plan")]
    public async Task PlanningUsesPostAndGuestAntiforgeryWithoutSendingAuthority(bool guest, string path)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/antiforgery", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { requestToken = "synthetic-token" }) };
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(path, request.RequestUri!.AbsolutePath);
            if (guest) Assert.True(request.Headers.Contains("X-CSRF-TOKEN"));
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var json = System.Text.Json.JsonDocument.Parse(body);
            Assert.Single(json.RootElement.EnumerateObject());
            Assert.Equal("proposal", json.RootElement.GetProperty("proposalId").GetString());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new EllaiGuidancePlanDto("Generating")) };
        })) { BaseAddress = new Uri("https://localhost/") };
        var result = await new EllaiReviewHttpClient(http, guest).PrepareGuidancePlanAsync("session", "proposal", default);
        Assert.Equal("Generating", result!.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RevalidationPostsExistingPlanAndStepWithoutRequestingANewPlan(bool guest)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { requestToken = "synthetic-token" }) };
            Assert.Equal($"/api/v1/ellai/{(guest ? "guest/" : "")}sessions/session/guidance/plan", request.RequestUri!.AbsolutePath);
            var payload = request.Content!.ReadFromJsonAsync<EllaiGuidancePlanRequest>().GetAwaiter().GetResult();
            Assert.Equal(new EllaiGuidancePlanRequest("proposal") { ExistingPlanId = "plan", CurrentStepId = "step" }, payload);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new EllaiGuidancePlanDto("ReplanRequired")) };
        })) { BaseAddress = new Uri("https://localhost/") };
        var result = await new EllaiReviewHttpClient(http, guest).RevalidateGuidancePlanAsync("session", "proposal", "plan", "step", default);
        Assert.Equal("ReplanRequired", result!.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProgressUsesOwnedRouteAndPreservesVersionFence(bool guest)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/antiforgery", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { requestToken = "synthetic-token" }) };
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/api/v1/ellai/{(guest ? "guest/" : "")}sessions/session/guidance/steps/complete", request.RequestUri!.AbsolutePath);
            if (guest) Assert.True(request.Headers.Contains("X-CSRF-TOKEN"));
            var payload = request.Content!.ReadFromJsonAsync<EllaiGuidanceStepRequest>().GetAwaiter().GetResult();
            Assert.Equal(new EllaiGuidanceStepRequest("proposal", "plan", 4, "step", "context"), payload);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new EllaiGuidanceProgressDto("Completed", 5, null, ["step"])) };
        })) { BaseAddress = new Uri("https://localhost/") };
        var result = await new EllaiReviewHttpClient(http, guest).CompleteGuidanceStepAsync("session",
            new("proposal", "plan", 4, "step", "context"), default);
        Assert.Equal("Completed", result!.Status);
        Assert.Equal(5, result.Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LifecycleUsesOwnedRouteWithAntiforgeryAndVersion(bool guest)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/antiforgery", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { requestToken = "synthetic-token" }) };
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/api/v1/ellai/{(guest ? "guest/" : "")}sessions/session/guidance/control", request.RequestUri!.AbsolutePath);
            if (guest) Assert.True(request.Headers.Contains("X-CSRF-TOKEN"));
            Assert.Equal(new EllaiGuidanceControlRequest("proposal", "plan", 4, "cancel"),
                request.Content!.ReadFromJsonAsync<EllaiGuidanceControlRequest>().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new EllaiGuidanceProgressDto("Cancelled", 5)) };
        })) { BaseAddress = new Uri("https://localhost/") };
        var result = await new EllaiReviewHttpClient(http, guest).ControlGuidanceAsync("session", new("proposal", "plan", 4, "cancel"), default);
        Assert.Equal("Cancelled", result!.Status);
        Assert.Equal(5, result.Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BrowserReceiptProofIsResolvedLocallyAndNeverIncludedInJson(bool available)
    {
        var posts = 0;
        var expected = new EllaiGuidanceReceiptRequest("proposal", "plan", 4, "step", Guid.NewGuid(), Guid.NewGuid());
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { requestToken = "synthetic-token" }) };
            posts++;
            Assert.Equal("synthetic-private-proof", Assert.Single(request.Headers.GetValues("X-SwiftAid-Receipt-Proof")));
            Assert.Equal(expected, request.Content!.ReadFromJsonAsync<EllaiGuidanceReceiptRequest>().GetAwaiter().GetResult());
            Assert.DoesNotContain("synthetic-private-proof", request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new EllaiGuidanceProgressDto("Completed", 5)) };
        })) { BaseAddress = new Uri("https://localhost/") };
        IEllaiReviewClient client = new EllaiReviewHttpClient(http, true, receiptProofResolver: (request, _) =>
        {
            Assert.Equal(expected, request);
            return Task.FromResult<string?>(available ? "synthetic-private-proof" : null);
        });
        var result = await client.CompleteGuidanceReceiptAsync("session", expected, default);
        Assert.Equal(available ? "Completed" : null, result?.Status);
        Assert.Equal(available ? 1 : 0, posts);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
