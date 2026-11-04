using System.Net;
using System.Net.Http.Json;
using SwiftAid.Client.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Tests;

public sealed class CaseQueueHttpClientTests
{
    [Fact]
    public async Task CursorIsEscapedOnRequestAndNextCursorIsReadFromResponse()
    {
        var returnedCursor = $"{DateTimeOffset.UtcNow.UtcDateTime.Ticks:x}.{Guid.NewGuid():N}";
        var handler = new RecordingHandler(new CaseQueueResult(CaseQueueAccess.Allowed, [], returnedCursor));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://gateway.example/") };
        var client = new CaseQueueHttpClient(http);
        var requestCursor = $"{DateTimeOffset.UtcNow.UtcDateTime.Ticks:x}.{Guid.NewGuid():N}";

        var result = await client.GetMineAsync(100, requestCursor);

        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Equal(returnedCursor, result.NextCursor);
        Assert.Contains("limit=100", handler.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal(requestCursor, Uri.UnescapeDataString(
            handler.RequestUri.Query.Split("cursor=", StringSplitOptions.None)[1]));
    }

    private sealed class RecordingHandler(CaseQueueResult response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(response)
            });
        }
    }
}
