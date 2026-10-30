using System.Net;
using System.Text;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Ui.Tests;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T08")]
public sealed class EllaiEventPollerTests
{
    private sealed class FakeReviews : IEllaiReviewClient
    {
        public EllaiSessionEventPageDto? Page { get; set; } =
            new EllaiSessionEventPageDto(Array.Empty<EllaiSessionEventDto>(), 0);

        public string? LastSession { get; private set; }

        public long LastCursor { get; private set; }

        public Task<EllaiSessionEventPageDto?> PollEventsAsync(
            string sessionId, long cursor, int waitSeconds, CancellationToken cancellationToken)
        {
            LastSession = sessionId;
            LastCursor = cursor;
            return Task.FromResult(Page);
        }

        public Task<EllaiSessionView?> StartSessionAsync(EllaiMode mode, CancellationToken cancellationToken) =>
            Task.FromResult<EllaiSessionView?>(null);

        public Task<EllaiReviewDto?> GetActiveReviewAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<EllaiReviewDto?>(null);

        public Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ApproveSelectedAsync(
            string sessionId, string requestId, IReadOnlyList<string> actionIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EllaiApprovedReceiptDto>?>(null);

        public Task<bool> RejectReviewAsync(
            string sessionId, string requestId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
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

        public string? LastMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.PathAndQuery;
            LastMethod = request.Method.Method;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task ApprovalRequiredFiresOncePerReview()
    {
        var reviews = new FakeReviews
        {
            Page = new EllaiSessionEventPageDto(new[]
            {
                new EllaiSessionEventDto(1, "s-1", "approval-required", "req-1", "hash v1", DateTimeOffset.UtcNow),
                new EllaiSessionEventDto(1, "s-1", "approval-required", "req-1", "hash v1", DateTimeOffset.UtcNow),
                new EllaiSessionEventDto(2, "s-1", "approval-required", "req-2", "hash v2", DateTimeOffset.UtcNow),
            }, 2),
        };
        var poller = new EllaiEventPoller(reviews);
        var fired = new List<string>();
        poller.ApprovalRequired += fired.Add;
        poller.TrackSession("s-1");

        await poller.PollOnceAsync(0, CancellationToken.None);

        Assert.Equal(new[] { "req-1", "req-2" }, fired);
        Assert.Equal(2, poller.Cursor);
        Assert.Equal("s-1", reviews.LastSession);
        Assert.Equal(0, reviews.LastCursor);
    }

    [Fact]
    public async Task SessionCancelFiresAndStaleEventsAreFenced()
    {
        var reviews = new FakeReviews
        {
            Page = new EllaiSessionEventPageDto(new[]
            {
                new EllaiSessionEventDto(5, "s-1", "session-cancelled", null, "Stopped.", DateTimeOffset.UtcNow),
            }, 5),
        };
        var poller = new EllaiEventPoller(reviews);
        var cancelled = 0;
        poller.SessionCancelled += () => cancelled++;
        poller.TrackSession("s-1");

        await poller.PollOnceAsync(0, CancellationToken.None);
        Assert.Equal(1, cancelled);

        reviews.Page = new EllaiSessionEventPageDto(new[]
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
        var reviews = new FakeReviews();
        var poller = new EllaiEventPoller(reviews);
        poller.TrackSession("s-1");
        await poller.PollOnceAsync(0, CancellationToken.None);

        poller.TrackSession("s-2");
        Assert.Equal(0, poller.Cursor);

        poller.ForgetSession();
        Assert.Null(poller.SessionId);
        await poller.PollOnceAsync(0, CancellationToken.None);
        Assert.Equal("s-1", reviews.LastSession);
    }

    [Fact]
    public async Task HttpClientParsesWirePayloadAndBuildsUrl()
    {
        const string json = """
            {"events":[{"sequence":3,"sessionId":"s-9","kind":"approval-required","ref":"req-9","detail":"hash v2","atUtc":"2026-10-30T08:00:00Z"}],"cursor":3}
            """;
        var handler = new FakeHandler(json);
        var client = new EllaiReviewHttpClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });

        var page = await client.PollEventsAsync("s-9", 0, 20, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(3, page.Cursor);
        var @event = Assert.Single(page.Events);
        Assert.Equal("req-9", @event.Ref);
        Assert.Equal("approval-required", @event.Kind);
        Assert.Contains("api/v1/ellai/sessions/s-9/events", handler.LastPath, StringComparison.Ordinal);
        Assert.Contains("cursor=0", handler.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpClientReturnsNullOnFailure()
    {
        var handler = new FakeHandler("{}", HttpStatusCode.Unauthorized);
        var client = new EllaiReviewHttpClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });

        Assert.Null(await client.PollEventsAsync("s-9", 0, 0, CancellationToken.None));
        Assert.Null(await client.StartSessionAsync(EllaiMode.Ask, CancellationToken.None));
        Assert.Null(await client.GetActiveReviewAsync("s-9", CancellationToken.None));
        Assert.Null(await client.ApproveSelectedAsync("s-9", "req-9", new[] { "a-1" }, CancellationToken.None));
        Assert.False(await client.RejectReviewAsync("s-9", "req-9", CancellationToken.None));
    }

    [Fact]
    public async Task HttpClientFetchesActiveReviewWithActions()
    {
        const string json = """
            {"requestId":"req-9","sessionId":"s-9","planHash":"hash-9","planVersion":2,"state":"AwaitingApproval","createdAtUtc":"2026-10-30T08:00:00Z","expiresAtUtc":"2026-10-30T08:15:00Z","actions":[{"actionId":"a-1","label":"Create review","detail":"kind"}]}
            """;
        var handler = new FakeHandler(json);
        var client = new EllaiReviewHttpClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });

        var review = await client.GetActiveReviewAsync("s-9", CancellationToken.None);

        Assert.NotNull(review);
        Assert.Equal("req-9", review.RequestId);
        Assert.Equal(2, review.PlanVersion);
        var action = Assert.Single(review.Actions);
        Assert.Equal("a-1", action.ActionId);
        Assert.Contains("approval-reviews/active", handler.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpClientApprovesSelectionAndRejectsReview()
    {
        const string approveJson = """[{"receiptId":"rcpt-1","actionId":"a-1","status":"Accepted"}]""";
        var handler = new FakeHandler(approveJson);
        var client = new EllaiReviewHttpClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });

        var receipts = await client.ApproveSelectedAsync("s-9", "req-9", new[] { "a-1" }, CancellationToken.None);

        Assert.NotNull(receipts);
        var receipt = Assert.Single(receipts);
        Assert.Equal("rcpt-1", receipt.ReceiptId);
        Assert.Equal("POST", handler.LastMethod);
        Assert.Contains("/approve", handler.LastPath, StringComparison.Ordinal);

        var rejectHandler = new FakeHandler("true");
        var rejectClient = new EllaiReviewHttpClient(new HttpClient(rejectHandler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });
        Assert.True(await rejectClient.RejectReviewAsync("s-9", "req-9", CancellationToken.None));
        Assert.Contains("/reject", rejectHandler.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpClientStartsSessionAndReturnsServerView()
    {
        const string json = """
            {"sessionId":"s-9","mode":0,"activeInvestigationId":null,"status":"Open","scopeDisplay":"Staff","consentVersion":"v1","capabilityVersion":"v1","cyclesUsed":0,"cyclesAllowed":100,"controlOwner":null,"lastEventSequence":0,"sessionVersion":1}
            """;
        var handler = new FakeHandler(json);
        var client = new EllaiReviewHttpClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });

        var view = await client.StartSessionAsync(EllaiMode.Ask, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal("s-9", view.SessionId);
        Assert.Equal(EllaiMode.Ask, view.Mode);
        Assert.Equal("POST", handler.LastMethod);
        Assert.Contains("api/v1/ellai/sessions", handler.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpClientStartSessionReturnsNullOnFailure()
    {
        var handler = new FakeHandler("{}", HttpStatusCode.Unauthorized);
        var client = new EllaiReviewHttpClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });

        Assert.Null(await client.StartSessionAsync(EllaiMode.Ask, CancellationToken.None));
    }
}
