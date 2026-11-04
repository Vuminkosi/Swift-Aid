using System.Net;
using System.Text;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Ui.Tests;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T08")]
public sealed class EllaiEventPollerTests
{
    [Theory]
    [InlineData(false, "Processing", true)]
    [InlineData(true, "Processing", false)]
    [InlineData(false, "Completed", false)]
    public async Task ProgressReconnectAndRefreshNeverReplayObsoleteSpeech(bool refresh, string outcome, bool expectedSpeech)
    {
        var reviews = new FakeReviews();
        var poller = new EllaiEventPoller(reviews);
        poller.TrackSession("s-1");
        var created = refresh ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow;
        reviews.ResponsePage = new([new(1, "s-1", "One moment.", created, "u", IsFinal: false,
            Progress: new("u:initial", "Acknowledgement", "Checking records", true, 1, DateTimeOffset.UtcNow.AddSeconds(8)))],
            1, "process", [new("u", 1, "Question", outcome == "Completed" ? "Answer" : null, outcome, 1, null)]);
        var received = new List<EllaiSessionResponseDto>();
        poller.ResponseReceived += received.Add;
        await poller.PollOnceAsync(0, CancellationToken.None);
        await poller.PollOnceAsync(0, CancellationToken.None);
        if (outcome == "Completed") Assert.Empty(received);
        else Assert.Equal(expectedSpeech, Assert.Single(received).Progress!.Speak);
        Assert.Equal(1, poller.ResponseCursor);
    }

    [Fact]
    public async Task GatewayRestartResetsTransientCursor()
    {
        var reviews = new FakeReviews
        {
            ResponsePage = new([new(8, "s-1", "Before restart", DateTimeOffset.UtcNow)], 8, "first-process"),
        };
        var poller = new EllaiEventPoller(reviews);
        var received = new List<EllaiSessionResponseDto>();
        poller.ResponseReceived += received.Add;
        poller.TrackSession("s-1");
        await poller.PollOnceAsync(0, CancellationToken.None);
        reviews.ResponsePage = new([new(1, "s-1", "After restart", DateTimeOffset.UtcNow)], 1, "second-process");
        await poller.PollOnceAsync(0, CancellationToken.None);
        Assert.Equal(2, received.Count);
        Assert.Equal("After restart", received[1].Text);
    }

    [Fact]
    public async Task FailedResponseHandlerDoesNotAdvanceCursor()
    {
        var reviews = new FakeReviews { ResponsePage = new([new(1, "s-1", "Answer", DateTimeOffset.UtcNow)], 1) };
        var poller = new EllaiEventPoller(reviews);
        poller.TrackSession("s-1");
        poller.ResponseReceived += _ => throw new InvalidOperationException("synthetic handler failure");
        await Assert.ThrowsAsync<InvalidOperationException>(() => poller.PollOnceAsync(0, CancellationToken.None));
        Assert.Equal(0, poller.ResponseCursor);
    }

    private sealed class FakeReviews : IEllaiReviewClient
    {
        public EllaiSessionEventPageDto? Page { get; set; } =
            new EllaiSessionEventPageDto(Array.Empty<EllaiSessionEventDto>(), 0);

        public EllaiSessionResponsePageDto? ResponsePage { get; set; } =
            new EllaiSessionResponsePageDto(Array.Empty<EllaiSessionResponseDto>(), 0);

        public string? LastSession { get; private set; }

        public long LastCursor { get; private set; }

        public Task<EllaiSessionResponsePageDto?> PollResponsesAsync(
            string sessionId, long cursor, CancellationToken cancellationToken) =>
            Task.FromResult(ResponsePage);

        public Task<EllaiSessionEventPageDto?> PollEventsAsync(
            string sessionId, long cursor, int waitSeconds, CancellationToken cancellationToken)
        {
            LastSession = sessionId;
            LastCursor = cursor;
            return Task.FromResult(Page);
        }

        public Task<EllaiSessionStartResult> StartSessionAsync(EllaiMode mode, CancellationToken cancellationToken) =>
            Task.FromResult(new EllaiSessionStartResult(EllaiClientOperationStatus.OutcomeUnknown));

        public Task<EllaiUtteranceSubmitResult> SubmitUtteranceAsync(
            SubmitUtteranceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.Unavailable, null));

        public Task<bool> GrantPublicProductHelpConsentAsync(
            string sessionId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<EllaiSessionCancelResult> CancelSessionAsync(
            string sessionId, long expectedSessionVersion, CancellationToken cancellationToken) =>
            Task.FromResult(new EllaiSessionCancelResult(EllaiClientOperationStatus.Unavailable));

        public Task<EllaiReviewDto?> GetActiveReviewAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<EllaiReviewDto?>(null);

        public Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ApproveSelectedAsync(
            string sessionId, string requestId, IReadOnlyList<string> actionIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EllaiApprovedReceiptDto>?>(null);

        public Task<bool> RejectReviewAsync(
            string sessionId, string requestId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> ReportUiObservationAsync(
            string sessionId, UiObservationReport report, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ListReceiptsAsync(
            string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EllaiApprovedReceiptDto>?>(null);
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

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.PathAndQuery;
            LastMethod = request.Method.Method;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class QueuedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<(string Method, string? Path, string? RequestToken)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.Method.Method,
                request.RequestUri?.PathAndQuery,
                request.Headers.TryGetValues("X-CSRF-TOKEN", out var values) ? values.Single() : null));
            return Task.FromResult(_responses.Dequeue());
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
    public async Task ActivityEventsUpdateStatusLineInSequenceOrder()
    {
        var reviews = new FakeReviews
        {
            Page = new EllaiSessionEventPageDto(new[]
            {
                new EllaiSessionEventDto(1, "s-1", "activity", "reading", "Checking SwiftAid's technical reference…", DateTimeOffset.UtcNow),
                new EllaiSessionEventDto(1, "s-1", "activity", "reading", "Checking SwiftAid's technical reference…", DateTimeOffset.UtcNow),
                new EllaiSessionEventDto(2, "s-1", "activity", "done", "Done — the result is verified.", DateTimeOffset.UtcNow),
            }, 2),
        };
        var poller = new EllaiEventPoller(reviews);
        var updates = new List<(string Phase, string Text)>();
        poller.ActivityUpdated += (phase, text) => updates.Add((phase, text));
        poller.TrackSession("s-1");

        await poller.PollOnceAsync(0, CancellationToken.None);

        Assert.Equal(
            new[] { ("reading", "Checking SwiftAid's technical reference…"), ("done", "Done — the result is verified.") },
            updates);
        Assert.Equal(2, poller.Cursor);
    }

    [Fact]
    public async Task ModelResponseIsDeliveredSeparatelyFromActivityAndDeduplicatedByCursor()
    {
        var response = new EllaiSessionResponseDto(1, "s-1", "A dynamic answer.", DateTimeOffset.UtcNow);
        var reviews = new FakeReviews
        {
            ResponsePage = new EllaiSessionResponsePageDto([response], 1),
        };
        var poller = new EllaiEventPoller(reviews);
        var received = new List<EllaiSessionResponseDto>();
        poller.ResponseReceived += received.Add;
        poller.TrackSession("s-1");

        await poller.PollOnceAsync(0, CancellationToken.None);
        await poller.PollOnceAsync(0, CancellationToken.None);

        Assert.Equal(response, Assert.Single(received));
        Assert.Equal(1, poller.ResponseCursor);
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
    public async Task HttpClientParsesSessionResponseAndBuildsCursorUrl()
    {
        const string json = """
            {"responses":[{"sequence":4,"sessionId":"s-9","text":"A dynamic answer.","createdAtUtc":"2026-10-30T08:00:00Z"}],"cursor":4}
            """;
        var handler = new FakeHandler(json);
        var client = new EllaiReviewHttpClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });

        var page = await client.PollResponsesAsync("s-9", 3, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(4, page.Cursor);
        var response = Assert.Single(page.Responses);
        Assert.Equal("A dynamic answer.", response.Text);
        Assert.Equal("s-9", response.SessionId);
        Assert.Equal("GET", handler.LastMethod);
        Assert.Contains("api/v1/ellai/sessions/s-9/responses?cursor=3", handler.LastPath, StringComparison.Ordinal);
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
        Assert.Equal(EllaiClientOperationStatus.Unauthorized,
            (await client.StartSessionAsync(EllaiMode.Ask, CancellationToken.None)).Status);
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

        var result = await client.StartSessionAsync(EllaiMode.Ask, CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.Accepted, result.Status);
        Assert.NotNull(result.Session);
        Assert.Equal("s-9", result.Session.SessionId);
        Assert.Equal(EllaiMode.Ask, result.Session.Mode);
        Assert.Equal("POST", handler.LastMethod);
        Assert.Contains("api/v1/ellai/sessions", handler.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuestHttpClientRefreshesAntiforgeryTokenAndRetriesOnlyAntiforgeryRejection()
    {
        const string sessionJson = """
            {"sessionId":"s-9","mode":0,"activeInvestigationId":null,"status":"Open","scopeDisplay":"Guest","consentVersion":"v1","capabilityVersion":"v1","cyclesUsed":0,"cyclesAllowed":100,"controlOwner":null,"lastEventSequence":0,"sessionVersion":1}
            """;
        var handler = new QueuedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"requestToken\":\"stale\"}", Encoding.UTF8, "application/json"),
            },
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"title\":\"The guest Ellai request could not be validated.\"}",
                    Encoding.UTF8,
                    "application/problem+json"),
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"requestToken\":\"fresh\"}", Encoding.UTF8, "application/json"),
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sessionJson, Encoding.UTF8, "application/json"),
            });
        var client = new EllaiReviewHttpClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        }, useGuestRoutes: true);

        var result = await client.StartSessionAsync(EllaiMode.Ask, CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.Accepted, result.Status);
        Assert.Equal("s-9", result.Session?.SessionId);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("POST", handler.Requests[1].Method);
        Assert.Equal("stale", handler.Requests[1].RequestToken);
        Assert.Equal("GET", handler.Requests[2].Method);
        Assert.Equal("POST", handler.Requests[3].Method);
        Assert.Equal("fresh", handler.Requests[3].RequestToken);
    }

    [Fact]
    public async Task HttpClientReportsUiObservation()
    {
        var handler = new FakeHandler("true");
        var client = new EllaiReviewHttpClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });

        var reported = await client.ReportUiObservationAsync(
            "s-9",
            new UiObservationReport("view-9", "ctx-9", "web", new[] { "report.time-location" }, CurrentNavigationId: "nav-report", CompletedControlIds: ["report.subject"])
                { ValidationInputIds = ["report.occurrence-date"],
                    InputStates = [new("report.time-location", "report.occurrence-date", UiInputPresence.Invalid)] },
            CancellationToken.None);

        Assert.True(reported);
        Assert.Equal("POST", handler.LastMethod);
        Assert.Contains("api/v1/ellai/sessions/s-9/ui-observations", handler.LastPath, StringComparison.Ordinal);
        Assert.Contains("\"currentNavigationId\":\"nav-report\"", handler.LastBody);
        Assert.Contains("\"completedControlIds\":[\"report.subject\"]", handler.LastBody);
        Assert.Contains("\"validationInputIds\":[\"report.occurrence-date\"]", handler.LastBody);
        Assert.Contains("\"inputStates\":[{\"controlId\":\"report.time-location\",\"inputId\":\"report.occurrence-date\",\"presence\":3}]", handler.LastBody);
    }

    [Fact]
    public async Task HttpClientListsReceiptsWithNamedStatuses()
    {
        const string json = """
            [{"receiptId":"rcpt-1","actionId":"a-1","status":"Accepted"},
             {"receiptId":"rcpt-2","actionId":"a-2","status":"Applied"}]
            """;
        var handler = new FakeHandler(json);
        var client = new EllaiReviewHttpClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });

        var list = await client.ListReceiptsAsync("s-9", CancellationToken.None);

        Assert.NotNull(list);
        Assert.Equal(2, list.Count);
        Assert.Equal("Accepted", list[0].Status);
        Assert.Equal("a-2", list[1].ActionId);
        Assert.Equal("GET", handler.LastMethod);
        Assert.Contains("api/v1/ellai/sessions/s-9/receipts", handler.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpClientListReceiptsReturnsNullOnFailure()
    {
        var handler = new FakeHandler("[]", HttpStatusCode.ServiceUnavailable);
        var client = new EllaiReviewHttpClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });

        Assert.Null(await client.ListReceiptsAsync("s-9", CancellationToken.None));
    }

    [Fact]
    public async Task HttpClientStartSessionDistinguishesDeniedFromAmbiguousFailure()
    {
        var handler = new FakeHandler("{}", HttpStatusCode.Unauthorized);
        var client = new EllaiReviewHttpClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });

        var denied = await client.StartSessionAsync(EllaiMode.Ask, CancellationToken.None);
        Assert.Equal(EllaiClientOperationStatus.Unauthorized, denied.Status);
        Assert.Null(denied.Session);

        var failedHandler = new FakeHandler("{}", HttpStatusCode.InternalServerError);
        var failedClient = new EllaiReviewHttpClient(new HttpClient(failedHandler)
        {
            BaseAddress = new Uri("https://loopback.test/"),
        });

        var unknown = await failedClient.StartSessionAsync(EllaiMode.Ask, CancellationToken.None);
        Assert.Equal(EllaiClientOperationStatus.OutcomeUnknown, unknown.Status);
        Assert.Null(unknown.Session);
    }
}
