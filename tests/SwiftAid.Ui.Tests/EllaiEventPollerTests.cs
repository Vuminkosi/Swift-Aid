using System.Net;
using System.Text;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Ui.Tests;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T08")]
public sealed class EllaiEventPollerTests
{
    private sealed class FakeTransport : IEllaiEventTransport
    {
        public EllaiSessionEventPageDto? Page { get; set; } =
            new EllaiSessionEventPageDto(Array.Empty<EllaiSessionEventDto>(), 0);

        public string? LastSession { get; private set; }

        public long LastCursor { get; private set; }

        public Task<EllaiSessionEventPageDto?> PollAsync(
            string sessionId, long cursor, int waitSeconds, CancellationToken cancellationToken)
        {
            LastSession = sessionId;
            LastCursor = cursor;
            return Task.FromResult(Page);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly HttpStatusCode _status;

        public FakeHandler(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _json = json;
            _status = status;
        }

        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task ApprovalRequiredFiresOncePerReview()
    {
        var transport = new FakeTransport
        {
            Page = new EllaiSessionEventPageDto(new[]
            {
                new EllaiSessionEventDto(1, "s-1", "approval-required", "req-1", "hash v1", DateTimeOffset.UtcNow),
                new EllaiSessionEventDto(1, "s-1", "approval-required", "req-1", "hash v1", DateTimeOffset.UtcNow),
                new EllaiSessionEventDto(2, "s-1", "approval-required", "req-2", "hash v2", DateTimeOffset.UtcNow),
            }, 2),
        };
        var poller = new EllaiEventPoller(transport);
        var fired = new List<string>();
        poller.ApprovalRequired += fired.Add;
        poller.TrackSession("s-1");

        await poller.PollOnceAsync(0, CancellationToken.None);

        Assert.Equal(new[] { "req-1", "req-2" }, fired);
        Assert.Equal(2, poller.Cursor);
        Assert.Equal("s-1", transport.LastSession);
        Assert.Equal(0, transport.LastCursor);
    }

    [Fact]
    public async Task SessionCancelFiresAndStaleEventsAreFenced()
    {
        var transport = new FakeTransport
        {
            Page = new EllaiSessionEventPageDto(new[]
            {
                new EllaiSessionEventDto(5, "s-1", "session-cancelled", null, "Stopped.", DateTimeOffset.UtcNow),
            }, 5),
        };
        var poller = new EllaiEventPoller(transport);
        var cancelled = 0;
        poller.SessionCancelled += () => cancelled++;
        poller.TrackSession("s-1");

        await poller.PollOnceAsync(0, CancellationToken.None);
        Assert.Equal(1, cancelled);

        transport.Page = new EllaiSessionEventPageDto(new[]
        {
            new EllaiSessionEventDto(4, "s-1", "approval-required", "req-old", "hash v1", DateTimeOffset.UtcNow),
        }, 4);
        var fired = new List<string>();
        poller.ApprovalRequired += fired.Add;
        await poller.PollOnceAsync(0, CancellationToken.None);
        Assert.Empty(fired);
    }

    [Fact]
    public async Task SessionChangeResetsCursorAndSeenReviews()
    {
        var transport = new FakeTransport();
        var poller = new EllaiEventPoller(transport);
        poller.TrackSession("s-1");
        await poller.PollOnceAsync(0, CancellationToken.None);

        poller.TrackSession("s-2");
        Assert.Equal(0, poller.Cursor);

        poller.ForgetSession();
        Assert.Null(poller.SessionId);
        await poller.PollOnceAsync(0, CancellationToken.None);
        Assert.Equal("s-1", transport.LastSession);
    }

    [Fact]
    public async Task HttpTransportParsesWirePayloadAndBuildsUrl()
    {
        const string json = """
            {"events":[{"sequence":3,"sessionId":"s-9","kind":"approval-required","ref":"req-9","detail":"hash v2","atUtc":"2026-10-30T08:00:00Z"}],"cursor":3}
            """;
        var handler = new FakeHandler(json);
        var transport = new EllaiEventHttpTransport(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });

        var page = await transport.PollAsync("s-9", 0, 20, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(3, page.Cursor);
        var @event = Assert.Single(page.Events);
        Assert.Equal("req-9", @event.Ref);
        Assert.Equal("approval-required", @event.Kind);
        Assert.Contains("api/v1/ellai/sessions/s-9/events", handler.LastPath, StringComparison.Ordinal);
        Assert.Contains("cursor=0", handler.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransportReturnsNullOnFailure()
    {
        var handler = new FakeHandler("{}", HttpStatusCode.Unauthorized);
        var transport = new EllaiEventHttpTransport(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });

        Assert.Null(await transport.PollAsync("s-9", 0, 0, CancellationToken.None));
    }
}
