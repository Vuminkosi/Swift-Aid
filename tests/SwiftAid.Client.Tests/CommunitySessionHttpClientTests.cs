using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SwiftAid.Client.Sessions;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Client.Tests;

public sealed class CommunitySessionHttpClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid KnownSessionId = Guid.Parse("d0c00001-2222-4a44-9aa9-123456789abc");

    [Fact]
    public async Task TypedPublicDetail_DistinguishesMissingFromOutage()
    {
        var missing = await Create(new HttpResponseMessage(HttpStatusCode.NotFound)).GetPublicResultAsync(KnownSessionId);
        var unavailable = await Create(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)).GetPublicResultAsync(KnownSessionId);

        Assert.True(missing.Succeeded);
        Assert.Null(missing.Value);
        Assert.False(unavailable.Succeeded);
        Assert.Null(unavailable.Value);
    }

    [Fact]
    public async Task TypedPublicDetail_ReturnsRequestedProjection()
    {
        var view = PublishedView();
        var handler = new CapturingHandler(JsonResponse(HttpStatusCode.OK, view));
        var client = new CommunitySessionHttpClient(new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        var result = await client.GetPublicResultAsync(KnownSessionId);

        Assert.True(result.Succeeded);
        Assert.Equal(KnownSessionId, result.Value!.SessionId);
        Assert.Equal($"/public/v1/sessions/{KnownSessionId:D}", handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task PublicClientRejectsMismatchedDetailAndUnpublishedRecords()
    {
        var wrong = Create(JsonResponse(HttpStatusCode.OK, PublishedView() with { SessionId = Guid.NewGuid() }));
        var mismatch = await wrong.GetPublicResultAsync(KnownSessionId);
        Assert.False(mismatch.Succeeded);
        Assert.Null(mismatch.Value);

        var draft = PublishedView() with { Status = "Draft" };
        var list = Create(JsonResponse(HttpStatusCode.OK, new[] { draft }));
        Assert.False((await list.ListPublicResultAsync(null)).Succeeded);
        var query = Create(JsonResponse(HttpStatusCode.OK, new PublicSessionPage([draft], null)));
        Assert.False((await query.QueryPublicResultAsync(new PublicSessionQuery())).Succeeded);
    }

    [Fact]
    public async Task NullListIsUnavailableRatherThanAnEmptyCollection()
    {
        var client = Create(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        });
        var result = await client.ListPublicResultAsync(null);
        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task QueryPublic_EncodesSearchAndReturnsContinuation()
    {
        var view = PublishedView();
        var page = new PublicSessionPage([view], new PublicSessionPosition(view.StartUtc, view.SessionId));
        var response = JsonResponse(HttpStatusCode.OK, page);
        response.Headers.Add(PublicSessionQueryFeatures.HeaderName, PublicSessionQueryFeatures.StatusFilter);
        var handler = new CapturingHandler(response);
        var client = new CommunitySessionHttpClient(new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        var result = await client.QueryPublicResultAsync(new PublicSessionQuery(Search: "water & health", Sector: "Human Health", Topic: "Water & food", Language: "en", Limit: 1, Category: "Food & water", LocalDateFrom: new DateOnly(2026, 11, 1), Status: "Completed"));

        Assert.True(result.Succeeded);
        Assert.Equal(view.SessionId, Assert.Single(result.Value!.Items).SessionId);
        Assert.Equal(view.SessionId, result.Value.Next!.SessionId);
        Assert.Equal("/public/v1/sessions/search?search=water%20%26%20health&sector=Human%20Health&language=en&topic=Water%20%26%20food&category=Food%20%26%20water&status=Completed&localFrom=2026-11-01&limit=1", handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task StatusFilterFailsClosedWhenOlderServerDoesNotConfirmSupport()
    {
        var page = new PublicSessionPage([PublishedView()], null);
        var client = Create(JsonResponse(HttpStatusCode.OK, page));
        var result = await client.QueryPublicResultAsync(new PublicSessionQuery(Status: "Completed"));

        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task QueriesWithoutStatusRemainCompatibleWithOlderServers()
    {
        var page = new PublicSessionPage([PublishedView()], null);
        var client = Create(JsonResponse(HttpStatusCode.OK, page));
        var result = await client.QueryPublicResultAsync(new PublicSessionQuery());

        Assert.True(result.Succeeded);
        Assert.Single(result.Value!.Items);
    }

    [Fact]
    public async Task QueryPublic_ReportsServiceFailure()
    {
        var client = Create(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var result = await client.QueryPublicResultAsync(new PublicSessionQuery());
        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task ListPublic_ReturnsSessions()
    {
        var view = PublishedView();
        var handler = new CapturingHandler(JsonResponse(HttpStatusCode.OK,
            (IReadOnlyList<PublicSessionView>)[view]));
        var client = new CommunitySessionHttpClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        var result = await client.ListPublicAsync(null);

        var listed = Assert.Single(result);
        Assert.Equal(view.SessionId, listed.SessionId);
        Assert.Equal(view.Title, listed.Title);
        Assert.Equal(view.Status, listed.Status);
        Assert.Equal("/public/v1/sessions", handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task ListPublic_AppendsLimit()
    {
        var handler = new CapturingHandler(JsonResponse(HttpStatusCode.OK,
            (IReadOnlyList<PublicSessionView>)[]));
        var client = new CommunitySessionHttpClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        await client.ListPublicAsync(25);

        Assert.Equal("/public/v1/sessions?limit=25", handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task ListPublic_MapsFailureToEmpty()
    {
        var client = Create(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await client.ListPublicAsync(null);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ListPublic_WithoutBaseAddress_ReturnsEmpty()
    {
        var client = new CommunitySessionHttpClient(
            new HttpClient(new CapturingHandler(JsonResponse(HttpStatusCode.OK,
                (IReadOnlyList<PublicSessionView>)[]))));

        var result = await client.ListPublicAsync(null);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPublic_ReturnsProjection()
    {
        var view = PublishedView();
        var handler = new CapturingHandler(JsonResponse(HttpStatusCode.OK, view));
        var client = new CommunitySessionHttpClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        var result = await client.GetPublicAsync(KnownSessionId);

        Assert.NotNull(result);
        Assert.Equal(view.SessionId, result!.SessionId);
        Assert.Equal(view.Title, result.Title);
        Assert.Equal(view.Status, result.Status);
        Assert.Equal($"/public/v1/sessions/{KnownSessionId:D}",
            handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task GetPublic_MapsNotFoundToNull()
    {
        var client = Create(new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await client.GetPublicAsync(KnownSessionId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetInvitationText_ReturnsText()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("You are invited: Dry-season holding hygiene", Encoding.UTF8, "text/plain")
        });
        var client = new CommunitySessionHttpClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        var result = await client.GetInvitationTextAsync(KnownSessionId);

        Assert.Contains("Dry-season holding hygiene", result);
        Assert.Equal($"/public/v1/sessions/{KnownSessionId:D}/invitation",
            handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task GetInvitationText_MapsNotFoundToNull()
    {
        var client = Create(new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await client.GetInvitationTextAsync(KnownSessionId);

        Assert.Null(result);
    }

    [Fact]
    public async Task VersionedInvitationRequiresServerConfirmation()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Invitation") };
        response.Headers.Add(PublicSessionInvitationFeatures.HeaderName, PublicSessionInvitationFeatures.Version);
        var handler = new CapturingHandler(response);
        var client = new CommunitySessionHttpClient(new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        var current = await client.GetInvitationTextForVersionResultAsync(KnownSessionId, 3);
        Assert.True(current.Succeeded);
        Assert.Equal("Invitation", current.Value);
        Assert.Equal($"/public/v1/sessions/{KnownSessionId:D}/invitation?version=3", handler.LastRequest!.RequestUri!.PathAndQuery);

        var olderServer = Create(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Unverified invitation") });
        var unverified = await olderServer.GetInvitationTextForVersionResultAsync(KnownSessionId, 3);
        Assert.False(unverified.Succeeded);
        Assert.Null(unverified.Value);
    }

    [Fact]
    public async Task GetCalendarFile_ReturnsDownload()
    {
        var ics = "BEGIN:VCALENDAR\r\nUID:swiftaid-community-session-test\r\nSEQUENCE:1\r\nEND:VCALENDAR\r\n";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ics, Encoding.UTF8, "text/calendar")
        });
        var client = new CommunitySessionHttpClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        var result = await client.GetCalendarFileAsync(KnownSessionId);

        Assert.NotNull(result);
        Assert.Equal($"session-{KnownSessionId:D}.ics", result!.FileName);
        Assert.Equal("text/calendar", result.ContentType);
        Assert.Equal(ics, Encoding.UTF8.GetString(result.Content));
        Assert.Equal($"/public/v1/sessions/{KnownSessionId:D}/calendar",
            handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task GetCalendarFile_MapsNotFoundToNull()
    {
        var client = Create(new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await client.GetCalendarFileAsync(KnownSessionId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCalendarFileRejectsUnexpectedTypeAndOversizedContent()
    {
        var html = Create(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>error</html>", Encoding.UTF8, "text/html")
        });
        Assert.Null(await html.GetCalendarFileAsync(KnownSessionId));

        var oversized = new ByteArrayContent(new byte[256 * 1024 + 1]);
        oversized.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/calendar");
        var large = Create(new HttpResponseMessage(HttpStatusCode.OK) { Content = oversized });
        Assert.Null(await large.GetCalendarFileAsync(KnownSessionId));
    }

    private static PublicSessionView PublishedView() => new(
        KnownSessionId,
        3,
        "Dry-season holding hygiene",
        "Practical steps for clean holding areas.",
        "Livestock keepers near Harare showgrounds",
        ["hygiene"],
        "ZW",
        "region-zw-harare",
        "Animal",
        "officer-2",
        "en",
        new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero),
        "Africa/Harare",
        60,
        40,
        [new StoredSessionMaterial("mat-hygiene-1", "v2")],
        SessionChannelType.None,
        null,
        "Scheduled",
        null,
        1);

    private static CommunitySessionHttpClient Create(HttpResponseMessage response) =>
        new(new HttpClient(new CapturingHandler(response)) { BaseAddress = new Uri("https://gateway.test/") });

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T value) =>
        new(status) { Content = JsonContent.Create(value, options: JsonOptions) };

    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }
}
