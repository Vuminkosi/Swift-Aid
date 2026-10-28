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
