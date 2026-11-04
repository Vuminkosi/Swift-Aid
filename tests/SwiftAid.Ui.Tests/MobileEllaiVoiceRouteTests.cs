using System.Net;
using System.Text;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Mobile.Services.Authentication;
using SwiftAid.Mobile.Services.Ellai;

namespace SwiftAid.Ui.Tests;

public sealed class MobileEllaiVoiceRouteTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistorySessionKeepsItsRouteWhenSignInChanges(bool signedIn)
    {
        var accountHandler = new Handler();
        var guestHandler = new Handler();
        using var account = new HttpClient(accountHandler) { BaseAddress = new Uri("https://example.test/") };
        using var guest = new HttpClient(guestHandler) { BaseAddress = new Uri("https://example.test/") };
        var authentication = new Authentication { SignedIn = signedIn };
        IEllaiReviewClient client = new MobileEllaiReviewClient(account, guest, authentication);

        Assert.NotNull(await client.ListSessionHistoryAsync(0, default));
        authentication.SignedIn = !signedIn;
        await client.ResumeSessionAsync("history-session", default);

        var expectedHandler = signedIn ? accountHandler : guestHandler;
        var otherHandler = signedIn ? guestHandler : accountHandler;
        Assert.Contains(signedIn ? "/api/v1/ellai/sessions/history-session" : "/api/v1/ellai/guest/sessions/history-session", expectedHandler.Paths);
        Assert.Empty(otherHandler.Paths);
    }

    [Fact]
    public async Task AccountSessionRemainsOnAccountRouteAfterSignOut()
    {
        var authenticatedHandler = new Handler();
        var guestHandler = new Handler();
        using var authenticated = new HttpClient(authenticatedHandler) { BaseAddress = new Uri("https://example.test/") };
        using var guest = new HttpClient(guestHandler) { BaseAddress = new Uri("https://example.test/") };
        var authentication = new Authentication { SignedIn = true };
        IEllaiReviewClient client = new MobileEllaiReviewClient(authenticated, guest, authentication);

        Assert.Equal(EllaiClientOperationStatus.Accepted, (await client.StartSessionAsync(EllaiMode.Ask, default)).Status);
        authentication.SignedIn = false;
        await client.ResumeSessionAsync("fixture-session", default);

        Assert.Contains("/api/v1/ellai/sessions/fixture-session", authenticatedHandler.Paths);
        Assert.Empty(guestHandler.Paths);
    }

    [Fact]
    public async Task CancelledGuestSessionRemainsOnGuestRouteAfterSignIn()
    {
        var authenticatedHandler = new Handler();
        var guestHandler = new Handler();
        using var authenticated = new HttpClient(authenticatedHandler) { BaseAddress = new Uri("https://example.test/") };
        using var guest = new HttpClient(guestHandler) { BaseAddress = new Uri("https://example.test/") };
        var authentication = new Authentication();
        IEllaiReviewClient client = new MobileEllaiReviewClient(authenticated, guest, authentication);

        Assert.Equal(EllaiClientOperationStatus.Accepted, (await client.StartSessionAsync(EllaiMode.Ask, default)).Status);
        authentication.SignedIn = true;
        Assert.Equal(EllaiClientOperationStatus.Cancelled, (await client.CancelSessionAsync("fixture-session", 1, default)).Status);
        await client.ResumeSessionAsync("fixture-session", default);

        Assert.Contains("/api/v1/ellai/guest/sessions/fixture-session", guestHandler.Paths);
        Assert.Empty(authenticatedHandler.Paths);
    }

    [Theory]
    [InlineData(false, "/api/v1/ellai/guest/sessions/fixture-session/voice/live-stream")]
    [InlineData(true, "/api/v1/ellai/sessions/fixture-session/voice/live-stream")]
    public async Task LiveVoiceUsesSessionRouteAndPreservesSpeechPreferences(bool signedIn, string expectedPath)
    {
        using var authenticated = new HttpClient(new Handler()) { BaseAddress = new Uri("https://example.test/") };
        using var guest = new HttpClient(new Handler()) { BaseAddress = new Uri("https://example.test/") };
        var authentication = new Authentication { SignedIn = signedIn };
        IEllaiReviewClient client = new MobileEllaiReviewClient(authenticated, guest, authentication);
        client.ConfigureSpeechPreferences(new EllaiLanguagePreferences("eng", "nya", "am_michael"));

        var started = await client.StartSessionAsync(EllaiMode.Ask, default);
        Assert.Equal(EllaiClientOperationStatus.Accepted, started.Status);
        authentication.SignedIn = !signedIn;
        var uri = client.CreateLiveVoiceWebSocketUri(new Uri("https://example.test/"), "fixture-session", "capture", 240);

        Assert.Equal("wss", uri.Scheme);
        Assert.Equal(expectedPath, uri.AbsolutePath);
        Assert.Contains("voiceCaptureOffsetSamples=240", uri.Query);
        Assert.Contains("outputLanguage=nya", uri.Query);
        Assert.Contains("englishVoiceId=am_michael", uri.Query);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            var body = request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/sessions", StringComparison.Ordinal)
                ? "{\"sessions\":[{\"sessionId\":\"history-session\",\"mode\":\"Ask\",\"status\":\"Active\",\"updatedAtUtc\":\"2026-01-01T00:00:00Z\"}],\"nextOffset\":null}"
                : request.RequestUri!.AbsolutePath.EndsWith("/antiforgery", StringComparison.Ordinal)
                ? "{\"requestToken\":\"fixture-token\"}"
                : "{\"sessionId\":\"fixture-session\",\"status\":\"Active\",\"sessionVersion\":1}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Authentication : IMobileAuthenticationSession
    {
        public bool SignedIn { get; set; }
        public event EventHandler? AuthenticationStateChanged { add { } remove { } }
        public Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default) => Task.FromResult(SignedIn);
        public Task<bool> HasPendingSignOutAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> SignInAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<MobileAuthenticationSignOutResult> SignOutAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MobileAuthenticationSignOutResult(false, false));
    }
}
