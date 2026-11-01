using System.Net;
using System.Net.Http.Json;
using SwiftAid.Client.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Tests;

public sealed class CaseClassificationHttpClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CaseClassificationAccess.Denied)]
    [InlineData(HttpStatusCode.Forbidden, CaseClassificationAccess.Denied)]
    [InlineData(HttpStatusCode.NotFound, CaseClassificationAccess.Denied)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CaseClassificationAccess.Unavailable)]
    public async Task Failure_never_returns_evidence(HttpStatusCode status, CaseClassificationAccess expected)
    {
        using var http = new HttpClient(new Handler(new(status))) { BaseAddress = new("https://fixture.test/") };
        var result = await new CaseClassificationHttpClient(http).GetAsync(Guid.NewGuid());
        Assert.Equal(expected, result.Access);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Successful_response_requires_exact_case_identity(bool matching)
    {
        var id = Guid.NewGuid();
        var snapshot = new CaseClassificationSnapshot(matching ? id : Guid.NewGuid(), []);
        using var http = new HttpClient(new Handler(new(HttpStatusCode.OK) { Content = JsonContent.Create(snapshot) })) { BaseAddress = new("https://fixture.test/") };
        Assert.Equal(matching ? CaseClassificationAccess.Available : CaseClassificationAccess.Unavailable,
            (await new CaseClassificationHttpClient(http).GetAsync(id)).Access);
    }

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
