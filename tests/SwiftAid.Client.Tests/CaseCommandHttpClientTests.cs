using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Client.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Tests;

public sealed class CaseCommandHttpClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Send_WithExplicitVersion_PostsWireBodyAndMapsAccepted()
    {
        var caseId = Guid.NewGuid();
        var receiptCommandId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var handler = new ScriptedHandler([
            JsonResponse(HttpStatusCode.OK, new
            {
                caseId,
                commandId = receiptCommandId,
                eventIds = new[] { eventId },
                aggregateVersion = 8,
                reportRevision = 2,
            }),
        ]);
        var client = Create(handler);
        var input = new CaseCommandInput(
            "ReviewProtectedAction", null, "Reviewing the request.",
            "officer-2", Guid.NewGuid(), "Approved", 7);

        var result = await client.SendAsync(caseId, input);

        Assert.Equal(CaseCommandStatus.Accepted, result.Status);
        Assert.Equal(receiptCommandId, result.CommandId);
        Assert.Equal([eventId], result.EventIds);
        Assert.Equal(8, result.AggregateVersion);
        Assert.Equal(2, result.ReportRevision);

        var post = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal($"/api/v1/cases/{caseId:D}/commands", post.RequestUri!.PathAndQuery);

        using var body = JsonDocument.Parse(await post.Content!.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ReviewProtectedAction", root.GetProperty("commandType").GetString());
        Assert.True(root.GetProperty("commandId").GetGuid() != Guid.Empty);
        Assert.Equal(7, root.GetProperty("expectedVersion").GetInt64());
        Assert.Equal("Reviewing the request.", root.GetProperty("reason").GetString());
        Assert.Equal("officer-2", root.GetProperty("proposedOwnerId").GetString());
        Assert.Equal(input.RequestId!.Value, root.GetProperty("requestId").GetGuid());
        Assert.Equal("Approved", root.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Send_WithoutVersion_ResolvesVersionFromTimelineFirst()
    {
        var caseId = Guid.NewGuid();
        var header = new CaseTimelineHeader(
            caseId, "org-zw-1", "workspace-1", "ZW", "Active", "officer-1", 6, 1,
            DateTimeOffset.UtcNow);
        var receiptCommandId = Guid.NewGuid();
        var handler = new ScriptedHandler([
            JsonResponse(HttpStatusCode.OK, new CaseTimeline(header, [])),
            JsonResponse(HttpStatusCode.OK, new
            {
                caseId,
                commandId = receiptCommandId,
                eventIds = new[] { Guid.NewGuid() },
                aggregateVersion = 7,
                reportRevision = 1,
            }),
        ]);
        var client = Create(handler);
        var input = new CaseCommandInput("Resolve", null, "Resolved.", null, null, null, null);

        var result = await client.SendAsync(caseId, input);

        Assert.Equal(CaseCommandStatus.Accepted, result.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(
            $"/api/v1/cases/{caseId:D}/timeline",
            handler.Requests[0].RequestUri!.PathAndQuery);

        using var body = JsonDocument.Parse(await handler.Requests[1].Content!.ReadAsStringAsync());
        Assert.Equal(6, body.RootElement.GetProperty("expectedVersion").GetInt64());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, CaseCommandStatus.Denied)]
    [InlineData(HttpStatusCode.Conflict, CaseCommandStatus.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, CaseCommandStatus.Invalid)]
    [InlineData(HttpStatusCode.InternalServerError, CaseCommandStatus.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CaseCommandStatus.Unavailable)]
    public async Task Send_MapsPostStatuses(HttpStatusCode status, CaseCommandStatus expected)
    {
        var handler = new ScriptedHandler([new HttpResponseMessage(status)]);
        var client = Create(handler);
        var input = new CaseCommandInput("Resolve", null, "Resolved.", null, null, null, 4);

        var result = await client.SendAsync(Guid.NewGuid(), input);

        Assert.Equal(expected, result.Status);
        Assert.Empty(result.EventIds);
    }

    [Fact]
    public async Task Send_ReturnsUnavailableOnMalformedSuccessBody()
    {
        var handler = new ScriptedHandler([
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{bad json") },
        ]);
        var client = Create(handler);
        var input = new CaseCommandInput("Resolve", null, "Resolved.", null, null, null, 4);

        var result = await client.SendAsync(Guid.NewGuid(), input);

        Assert.Equal(CaseCommandStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Send_InvalidInput_ReturnsInvalidWithoutRequest()
    {
        var handler = new ScriptedHandler([new HttpResponseMessage(HttpStatusCode.OK)]);
        var client = Create(handler);

        var result = await client.SendAsync(
            Guid.Empty,
            new CaseCommandInput("Resolve", null, "Resolved.", null, null, null, 4));

        Assert.Equal(CaseCommandStatus.Invalid, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Send_TimelineDenied_ReturnsDeniedWithoutPost()
    {
        var handler = new ScriptedHandler([new HttpResponseMessage(HttpStatusCode.NotFound)]);
        var client = Create(handler);
        var input = new CaseCommandInput("Resolve", null, "Resolved.", null, null, null, null);

        var result = await client.SendAsync(Guid.NewGuid(), input);

        Assert.Equal(CaseCommandStatus.Denied, result.Status);
        var get = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, get.Method);
    }

    [Fact]
    public async Task Send_WithoutBaseAddress_ReturnsUnavailable()
    {
        var client = new CaseCommandHttpClient(
            new HttpClient(new ScriptedHandler([new HttpResponseMessage(HttpStatusCode.OK)])));
        var input = new CaseCommandInput("Resolve", null, "Resolved.", null, null, null, 4);

        var result = await client.SendAsync(Guid.NewGuid(), input);

        Assert.Equal(CaseCommandStatus.Unavailable, result.Status);
    }

    private static CaseCommandHttpClient Create(ScriptedHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T value) =>
        new(status) { Content = JsonContent.Create(value, options: JsonOptions) };

    private sealed class ScriptedHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
