using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SwiftAid.Client.Authentication;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;
using SwiftAid.Mobile.Services.Http;
using SwiftAid.Mobile.Services.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class MobileGatewaySessionTests
{
    private static readonly Uri Gateway = new("https://gateway.test/");

    [Fact]
    public async Task AccountSignOutClearsAccountAndAntiforgeryCookiesButKeepsGuestEllaiSession()
    {
        var persistence = new RecordingCookiePersistence();
        using var session = new CookieSessionHttpClient(Gateway.ToString(), persistence);
        session.StoreCookies(Gateway,
        [
            "__Host-SwiftAid.Auth=account; Path=/; Secure; HttpOnly",
            ".SwiftAid.Ellai.Guest=guest; Path=/; Secure; HttpOnly",
            ".AspNetCore.Antiforgery.fixture=csrf; Path=/; Secure; HttpOnly",
            "other=account-data; Path=/; Secure"
        ]);

        await session.ClearAccountCookiesAsync();

        var cookies = session.GetCookieHeader(Gateway);
        Assert.Contains(".SwiftAid.Ellai.Guest=guest", cookies);
        Assert.DoesNotContain("__Host-SwiftAid.Auth", cookies);
        Assert.DoesNotContain("Antiforgery", cookies);
        Assert.DoesNotContain("other=", cookies);
        Assert.False(session.HasAuthenticationCookie);
        Assert.Equal(1, persistence.ClearCount);

        await session.ClearCookiesAsync();
        Assert.Empty(session.GetCookieHeader(Gateway));
    }

    [Fact]
    public async Task RelayUsesCookieOnlyWithoutBearerAndRejectsAnotherOrigin()
    {
        using var session = new CookieSessionHttpClient(Gateway.ToString());
        session.StoreCookies(Gateway, ["__Host-SwiftAid.Auth=synthetic; Path=/; Secure; HttpOnly"]);
        var terminal = new CaptureHandler();
        using var relay = new CookieSessionRelayHandler(session, new NativeSessionHttpOptions(Gateway)) { InnerHandler = terminal };
        using var client = new HttpClient(relay) { BaseAddress = Gateway };

        using var cookieResponse = await client.GetAsync("/api/v1/cases/mine");
        Assert.Contains("__Host-SwiftAid.Auth=synthetic", terminal.Cookie);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases/mine");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-token");
        using var bearerResponse = await client.SendAsync(request);
        Assert.Null(terminal.Cookie);
        Assert.Equal("Bearer", terminal.Authorization?.Scheme);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://other.test/api/v1/cases/mine"));
    }

    [Fact]
    public async Task CookieWritesRequireAntiforgeryWhileBearerWritesDoNot()
    {
        using var session = new FixtureCookieSession();
        session.StoreCookies(Gateway, ["__Host-SwiftAid.Auth=synthetic; Path=/; Secure; HttpOnly"]);
        var terminal = new CaptureHandler();
        using var relay = new CookieSessionRelayHandler(session, new NativeSessionHttpOptions(Gateway), protectCookieWrites: true) { InnerHandler = terminal };
        using var client = new HttpClient(relay) { BaseAddress = Gateway };

        using var response = await client.PostAsync("/api/v1/cases/fixture", null);
        Assert.Equal("synthetic-csrf", terminal.Antiforgery);
        Assert.Contains("__Host-SwiftAid.Auth=synthetic", terminal.Cookie);
        using var bearer = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases/fixture");
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-token");
        using var bearerResponse = await client.SendAsync(bearer);
        Assert.Null(terminal.Antiforgery);
        Assert.Null(terminal.Cookie);
        Assert.Equal(1, session.TokenRequests);

        session.Token = null;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("/api/v1/cases/fixture", null));
        Assert.Equal(2, terminal.RequestCount);
    }

    [Fact]
    public async Task ReportDetailsRequiresMatchingCaseAndFailsClosedOnDenial()
    {
        var id = Guid.NewGuid();
        var report = new CaseReportDetails(id, 1, "Synthetic observation", [], null);
        var handler = new CaptureHandler { Response = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(report) } };
        using var http = new HttpClient(handler) { BaseAddress = Gateway };
        var client = new CaseReportDetailsHttpClient(http);
        Assert.Equal(id, (await client.GetAsync(id)).Value?.CaseId);
        Assert.Equal(UiCapabilityResultCode.Retryable, (await client.GetAsync(Guid.NewGuid())).Code);
        handler.Response = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.GetAsync(id)).Code);
    }

    [Fact]
    public async Task SummaryRejectsAnotherCasesValidResponse()
    {
        var id = Guid.NewGuid();
        var summary = new CaseDetailSummary(id, "Case fixture", CaseWorkspaceSector.Human, "Summary", "Not recorded",
            "Open", "Normal", "Eswatini", "Not recorded", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var handler = new CaptureHandler { Response = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(summary) } };
        using var http = new HttpClient(handler) { BaseAddress = Gateway };
        var client = new CaseDetailSummaryHttpClient(http);
        Assert.Equal(id, (await client.GetAsync(id)).Value?.CaseId);
        Assert.Equal(UiCapabilityResultCode.Retryable, (await client.GetAsync(Guid.NewGuid())).Code);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Cookie { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Antiforgery { get; private set; }
        public int RequestCount { get; private set; }
        public Func<HttpResponseMessage> Response { get; set; } = () => new(HttpStatusCode.OK);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Cookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null;
            Authorization = request.Headers.Authorization;
            Antiforgery = request.Headers.TryGetValues("X-CSRF-TOKEN", out var tokens) ? tokens.Single() : null;
            RequestCount++;
            return Task.FromResult(Response());
        }
    }

    private sealed class RecordingCookiePersistence : IGatewayAuthCookiePersistence
    {
        public int ClearCount { get; private set; }
        public Task<GatewayAuthCookieSnapshot?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<GatewayAuthCookieSnapshot?>(null);
        public Task WriteAsync(GatewayAuthCookieSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            ClearCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixtureCookieSession() : CookieSessionHttpClient(Gateway.ToString())
    {
        public string? Token { get; set; } = "synthetic-csrf";
        public int TokenRequests { get; private set; }
        public override Task<string?> GetAntiforgeryTokenAsync(CancellationToken cancellationToken = default)
        {
            TokenRequests++;
            return Task.FromResult(Token);
        }
    }
}
