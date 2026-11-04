using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Client.Coordination;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Client.Tests;

public sealed class IntakeCoordinationHttpClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetQueue_ReturnsEntriesWhenAllowed()
    {
        var entries = new List<IntakeCoordinationQueueEntry>
        {
            new(Guid.NewGuid(), IntakeStatus.ReceivedUnverified, IntakeRoutingStatus.NeedsRouting,
                null, "ZW", "harare", "budiriro", "catalogue-1", IntakeSubjectScope.Human,
                DateTimeOffset.UtcNow, 1, null, false)
        };
        var client = Create(JsonResponse(HttpStatusCode.OK, entries));

        var (access, result) = await client.GetQueueAsync(null, 50);

        Assert.Equal(CoordinationQueueAccess.Allowed, access);
        Assert.Single(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CoordinationQueueAccess.Denied)]
    [InlineData(HttpStatusCode.NotFound, CoordinationQueueAccess.Denied)]
    [InlineData(HttpStatusCode.InternalServerError, CoordinationQueueAccess.Unavailable)]
    public async Task GetQueue_MapsAccessFailures(HttpStatusCode status, CoordinationQueueAccess expected)
    {
        var client = Create(new HttpResponseMessage(status));

        var (access, entries) = await client.GetQueueAsync(null, 50);

        Assert.Equal(expected, access);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetDetail_ReturnsNullOnNotFound()
    {
        var client = Create(new HttpResponseMessage(HttpStatusCode.NotFound));

        var detail = await client.GetDetailAsync(Guid.NewGuid());

        Assert.Null(detail);
    }

    [Fact]
    public async Task GetQueues_ReturnsOptions()
    {
        var options = new List<IntakeCoordinationQueueOption>
        {
            new("queue-zw-harare", "org-zw-1", "ZW", "catalogue-1")
        };
        var client = Create(JsonResponse(HttpStatusCode.OK, options));

        var result = await client.GetQueuesAsync();

        var option = Assert.Single(result);
        Assert.Equal("queue-zw-harare", option.QueueId);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, CoordinationActionOutcome.Applied)]
    [InlineData(HttpStatusCode.Conflict, CoordinationActionOutcome.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, CoordinationActionOutcome.Invalid)]
    [InlineData(HttpStatusCode.NotFound, CoordinationActionOutcome.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CoordinationActionOutcome.Unavailable)]
    public async Task Act_MapsStatuses(HttpStatusCode status, CoordinationActionOutcome expected)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") };
        var client = new IntakeCoordinationHttpClient(http);
        var receiptId = Guid.NewGuid();

        var outcome = await client.ActAsync(receiptId, 2, IntakeCoordinationAction.ClaimForReview, null, "Taking review.");

        Assert.Equal(expected, outcome);
        Assert.NotNull(handler.LastRequest);
        Assert.EndsWith($"/api/v1/intake/coordination/{receiptId:D}/actions", handler.LastRequest.RequestUri!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Act_RejectsInvalidInputLocally()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") };
        var client = new IntakeCoordinationHttpClient(http);

        var outcome = await client.ActAsync(Guid.Empty, 0, IntakeCoordinationAction.ClaimForReview, null, "  ");

        Assert.Equal(CoordinationActionOutcome.Invalid, outcome);
        Assert.Null(handler.LastRequest);
    }

    private static IntakeCoordinationHttpClient Create(HttpResponseMessage response) =>
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
