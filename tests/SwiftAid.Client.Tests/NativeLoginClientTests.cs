using Duende.IdentityModel.OidcClient.Browser;
using SwiftAid.Client.Authentication;

namespace SwiftAid.Client.Tests;

public sealed class NativeLoginClientTests
{
    private static readonly NativeLoginOptions Options = new(
        "https://identity.swiftaid.example",
        "swiftaid-native",
        "swiftaid:/auth/callback",
        "swiftaid:/auth/logout");

    [Fact]
    public async Task Login_SuccessfulProtocolResultStoresOnlySwiftAidAccessSession()
    {
        var protocol = new FakeOidcClient(new NativeOidcLoginResult(
            IsError: false,
            Error: null,
            AccessToken: "opaque-swiftaid-access-token",
            AccessTokenExpiration: DateTimeOffset.UtcNow.AddMinutes(10),
            IdentityToken: "ephemeral-swiftaid-id-token",
            RefreshToken: "opaque-swiftaid-refresh-token"));
        var store = new FakeSessionStore();
        var browser = new FakeBrowser();
        var client = Create(protocol, browser, store);

        var result = await client.LoginAsync(CancellationToken.None);

        Assert.Equal(NativeLoginStatus.Succeeded, result.Status);
        Assert.Equal("opaque-swiftaid-access-token", store.Session?.AccessToken);
        Assert.DoesNotContain("id-token", store.Session?.AccessToken, StringComparison.Ordinal);
        Assert.Same(browser, protocol.Browser);
    }

    [Theory]
    [InlineData("UserCancel", NativeLoginStatus.Cancelled)]
    [InlineData("access_denied", NativeLoginStatus.Failed)]
    [InlineData("invalid_response", NativeLoginStatus.Failed)]
    public async Task Login_ProtocolErrorNeverPersistsSession(string error, NativeLoginStatus expectedStatus)
    {
        var protocol = new FakeOidcClient(new NativeOidcLoginResult(
            IsError: true,
            Error: error,
            AccessToken: null,
            AccessTokenExpiration: default,
            IdentityToken: null));
        var store = new FakeSessionStore();
        var client = Create(protocol, new FakeBrowser(), store);

        var result = await client.LoginAsync(CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(store.Session);
        Assert.Equal(0, store.StoreCalls);
        Assert.DoesNotContain(error, result.ErrorCode ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_MalformedSuccessfulProtocolResultNeverPersistsSession()
    {
        var protocol = new FakeOidcClient(new NativeOidcLoginResult(
            IsError: false,
            Error: null,
            AccessToken: " ",
            AccessTokenExpiration: DateTimeOffset.UtcNow.AddMinutes(10),
            IdentityToken: null));
        var store = new FakeSessionStore();
        var client = Create(protocol, new FakeBrowser(), store);

        var result = await client.LoginAsync(CancellationToken.None);

        Assert.Equal(NativeLoginStatus.Failed, result.Status);
        Assert.Equal("invalid_sign_in_response", result.ErrorCode);
        Assert.Null(store.Session);
        Assert.Equal(0, store.StoreCalls);
    }

    [Fact]
    public async Task Login_SecureStorageFailureFailsClosedAndClearsPartialSession()
    {
        var protocol = new FakeOidcClient(new NativeOidcLoginResult(
            IsError: false,
            Error: null,
            AccessToken: "opaque-swiftaid-access-token",
            AccessTokenExpiration: DateTimeOffset.UtcNow.AddMinutes(10),
            IdentityToken: "ephemeral-swiftaid-id-token",
            RefreshToken: "opaque-swiftaid-refresh-token"));
        var store = new FakeSessionStore { StoreThrowsAfterWrite = true };
        var client = Create(protocol, new FakeBrowser(), store);

        var result = await client.LoginAsync(CancellationToken.None);

        Assert.Equal(NativeLoginStatus.Failed, result.Status);
        Assert.Equal("session_storage_failed", result.ErrorCode);
        Assert.Null(store.Session);
        Assert.Equal(1, store.ClearCalls);
    }

    [Fact]
    public async Task Logout_KeepsLockedSessionWhenRemoteSignOutUnconfirmed()
    {
        var protocol = new FakeOidcClient(new NativeOidcLoginResult(
            IsError: false,
            Error: null,
            AccessToken: "opaque-swiftaid-access-token",
            AccessTokenExpiration: DateTimeOffset.UtcNow.AddMinutes(10),
            IdentityToken: "ephemeral-swiftaid-id-token",
            RefreshToken: "opaque-swiftaid-refresh-token"))
        {
            LogoutThrows = true
        };
        var store = new FakeSessionStore();
        var client = Create(protocol, new FakeBrowser(), store);
        Assert.Equal(NativeLoginStatus.Succeeded, (await client.LoginAsync(CancellationToken.None)).Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LogoutAsync(CancellationToken.None));

        Assert.NotNull(store.Session);
        Assert.Equal(0, store.ClearCalls);
        Assert.Equal("ephemeral-swiftaid-id-token", protocol.LastLogoutHint);
    }

    [Fact]
    public async Task Logout_ClearsLocalSessionWhenRemoteSignOutConfirmed()
    {
        var protocol = new FakeOidcClient(new NativeOidcLoginResult(
            IsError: false,
            Error: null,
            AccessToken: "opaque-swiftaid-access-token",
            AccessTokenExpiration: DateTimeOffset.UtcNow.AddMinutes(10),
            IdentityToken: "ephemeral-swiftaid-id-token",
            RefreshToken: "opaque-swiftaid-refresh-token"));
        var store = new FakeSessionStore();
        var client = Create(protocol, new FakeBrowser(), store);
        Assert.Equal(NativeLoginStatus.Succeeded, (await client.LoginAsync(CancellationToken.None)).Status);

        await client.LogoutAsync(CancellationToken.None);

        Assert.Null(store.Session);
        Assert.Equal(1, store.ClearCalls);
        Assert.Equal("ephemeral-swiftaid-id-token", protocol.LastLogoutHint);
    }

    [Fact]
    public async Task Logout_CancelledCallerStillClearsAfterCancellingInFlightLogin()
    {
        var protocol = new FakeOidcClient(new NativeOidcLoginResult(
            IsError: false,
            Error: null,
            AccessToken: "unused",
            AccessTokenExpiration: DateTimeOffset.UtcNow.AddMinutes(10),
            IdentityToken: null))
        {
            WaitForLoginCancellation = true
        };
        var store = new FakeSessionStore();
        var client = Create(protocol, new FakeBrowser(), store);
        var loginTask = client.LoginAsync(CancellationToken.None);
        await protocol.LoginStarted.Task;
        using var logoutCancellation = new CancellationTokenSource();

        var logoutTask = client.LogoutAsync(logoutCancellation.Token);
        logoutCancellation.Cancel();
        await logoutTask;
        var loginResult = await loginTask;

        Assert.Equal(NativeLoginStatus.Cancelled, loginResult.Status);
        Assert.Null(store.Session);
        Assert.Equal(1, store.ClearCalls);
    }

    [Fact]
    public async Task Logout_InvalidatesLoginQueuedBeforeSessionClear()
    {
        var protocol = new FakeOidcClient(new NativeOidcLoginResult(
            IsError: false,
            Error: null,
            AccessToken: "unused",
            AccessTokenExpiration: DateTimeOffset.UtcNow.AddMinutes(10),
            IdentityToken: null))
        {
            WaitForLoginCancellation = true
        };
        var store = new FakeSessionStore();
        var client = Create(protocol, new FakeBrowser(), store);
        var firstLogin = client.LoginAsync(CancellationToken.None);
        await protocol.LoginStarted.Task;
        var queuedLogin = client.LoginAsync(CancellationToken.None);

        await client.LogoutAsync(CancellationToken.None);
        var firstResult = await firstLogin;
        var queuedResult = await queuedLogin;

        Assert.Equal(NativeLoginStatus.Cancelled, firstResult.Status);
        Assert.Equal(NativeLoginStatus.Cancelled, queuedResult.Status);
        Assert.Equal(1, protocol.LoginCalls);
        Assert.Null(store.Session);
        Assert.Equal(1, store.ClearCalls);
    }

    [Fact]
    public async Task Login_CancellationDoesNotPersistSession()
    {
        var protocol = new FakeOidcClient(new NativeOidcLoginResult(
            IsError: true,
            Error: "UserCancel",
            AccessToken: null,
            AccessTokenExpiration: default,
            IdentityToken: null))
        {};
        var store = new FakeSessionStore();
        var client = Create(protocol, new FakeBrowser(), store);

        var result = await client.LoginAsync(CancellationToken.None);

        Assert.Equal(NativeLoginStatus.Cancelled, result.Status);
        Assert.Null(store.Session);
        Assert.Equal(0, store.StoreCalls);
    }

    [Fact]
    public void Constructor_RejectsNonHttpsAuthority()
    {
        var invalidOptions = Options with { Authority = "http://identity.swiftaid.example" };

        Assert.Throws<ArgumentException>(() => new NativeLoginClient(
            invalidOptions,
            new FakeFactory(new FakeOidcClient(new NativeOidcLoginResult(
                IsError: true,
                Error: "unused",
                AccessToken: null,
                AccessTokenExpiration: default,
                IdentityToken: null))),
            new FakeBrowser(),
            new FakeSessionStore()));
    }

    [Fact]
    public void DuendeFactory_CreatesClientFromPublicAuthorityAndInjectedBrowser()
    {
        var browser = new FakeBrowser();

        var client = new DuendeNativeOidcClientFactory().Create(Options, browser);

        Assert.NotNull(client);
    }

    private static NativeLoginClient Create(FakeOidcClient protocol, FakeBrowser browser, FakeSessionStore store) =>
        new(Options, new FakeFactory(protocol), browser, store);

    private sealed class FakeFactory(FakeOidcClient protocol) : INativeOidcClientFactory
    {
        public INativeOidcClient Create(NativeLoginOptions options, IBrowser browser)
        {
            protocol.Browser = browser;
            return protocol;
        }
    }

    private sealed class FakeOidcClient(NativeOidcLoginResult result) : INativeOidcClient
    {
        public TaskCompletionSource LoginStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LoginCalls { get; private set; }

        public IBrowser? Browser { get; set; }

        public bool LogoutThrows { get; init; }

        public bool WaitForLoginCancellation { get; init; }

        public string? LastLogoutHint { get; private set; }

        public async Task<NativeOidcLoginResult> LoginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoginCalls++;
            LoginStarted.TrySetResult();
            if (WaitForLoginCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return result;
        }

        public Task<NativeOidcLoginResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
            return Task.FromResult(result);
        }

        public Task<bool> LogoutAsync(string? idTokenHint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastLogoutHint = idTokenHint;
            if (LogoutThrows)
            {
                throw new HttpRequestException("private provider failure");
            }

            return Task.FromResult(true);
        }
    }

    private sealed class FakeBrowser : IBrowser
    {
        public Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new BrowserResult
            {
                ResultType = BrowserResultType.Success,
                Response = "swiftaid://auth/callback?code=test&state=library-owned"
            });
        }
    }

    private sealed class FakeSessionStore : INativeSessionStore
    {
        public NativeLoginSession? Session { get; private set; }

        public Task<NativeLoginSession?> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Session);
        }

        public int StoreCalls { get; private set; }

        public int ClearCalls { get; private set; }

        public bool StoreThrowsAfterWrite { get; init; }

        public Task StoreAsync(NativeLoginSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Session = session;
            StoreCalls++;
            if (StoreThrowsAfterWrite)
            {
                throw new InvalidOperationException("private secure storage detail");
            }

            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Session = null;
            ClearCalls++;
            return Task.CompletedTask;
        }
    }
}
