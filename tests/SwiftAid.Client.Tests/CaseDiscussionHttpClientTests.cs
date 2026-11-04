using System.Net;
using System.Net.Http.Json;
using SwiftAid.Client.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class CaseDiscussionHttpClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, UiCapabilityResultCode.Denied)]
    [InlineData(HttpStatusCode.NotFound, UiCapabilityResultCode.Denied)]
    [InlineData(HttpStatusCode.Conflict, UiCapabilityResultCode.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, UiCapabilityResultCode.InvalidRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable, UiCapabilityResultCode.Unavailable)]
    public async Task Failed_http_status_is_never_presented_as_success(HttpStatusCode status, UiCapabilityResultCode expected)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(status))))
            { BaseAddress = new Uri("https://synthetic.example/") };
        var result = await new CaseDiscussionHttpClient(http).GetAsync(Guid.NewGuid(), new(new()));
        Assert.Equal(expected, result.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Post_preserves_case_version_and_retry_identity()
    {
        var command = new CaseDiscussionPostCommand(Guid.NewGuid(), Guid.NewGuid(), 12, "Delivery confirmed", [], []);
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/api/v1/cases/{command.CaseId:D}/discussion", request.RequestUri!.AbsolutePath);
            var payload = await request.Content!.ReadFromJsonAsync<CaseDiscussionPostCommand>();
            Assert.Equal(command.SubmissionId, payload!.SubmissionId);
            Assert.Equal(command.ExpectedVersion, payload.ExpectedVersion);
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        })) { BaseAddress = new Uri("https://synthetic.example/") };
        Assert.Equal(UiCapabilityResultCode.Conflict, (await new CaseDiscussionHttpClient(http).AppendAsync(command)).Code);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}
