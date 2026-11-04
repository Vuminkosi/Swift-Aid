using System.Net;
using System.Net.Http.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Mobile.Services.Account;

namespace SwiftAid.Client.Tests;

public sealed class MobileSubscriptionClientTests
{
    [Fact]
    public async Task UnauthorizedReadOffersSignInInsteadOfMismatchedSession()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)))
            { BaseAddress = new Uri("https://gateway.test/") };
        var result = await new HttpEventSubscriptionClient(http).GetAsync(Guid.NewGuid());
        Assert.Equal(UiCapabilityResultCode.Denied, result.Code);
        Assert.Equal(SubscriptionStatus.RequiresAuthentication, SessionSubscriptionPresentation.Status(result));
    }

    [Fact]
    public async Task SubscribeUsesAuthenticatedTransportAndPreservesConsentAndVersion()
    {
        var id = Guid.NewGuid();
        string? payload = null;
        var handler = new StubHandler(request =>
        {
            payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new EventSubscription(id, "fixture", EventSubscriptionStatus.Active, ["Email"], "consent", 2))
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") };
        var result = await new HttpEventSubscriptionClient(http).SubscribeAsync(new(id, "consent", 3, SubscriptionChannel.Email));
        Assert.True(result.Succeeded);
        Assert.Equal(id, result.Value?.SessionId);
        Assert.Contains("\"sessionVersion\":3", payload);
        Assert.Contains("\"consentReference\":\"consent\"", payload);
        Assert.Contains("\"channel\":\"Email\"", payload);
    }

    [Fact]
    public async Task RejectsWrongSessionAndUnknownSubscriptionStatus()
    {
        var id = Guid.NewGuid();
        var value = new EventSubscription(Guid.NewGuid(), "fixture", EventSubscriptionStatus.Active, ["Email"], "consent", 2);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") };
        var client = new HttpEventSubscriptionClient(http);
        Assert.Equal(UiCapabilityResultCode.Retryable, (await client.GetAsync(id)).Code);
        value = value with { SessionId = id, Status = (EventSubscriptionStatus)99 };
        Assert.Equal(UiCapabilityResultCode.Retryable, (await client.GetAsync(id)).Code);
        Assert.Equal(UiCapabilityResultCode.Retryable, (await client.UnsubscribeAsync(id, 2)).Code);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
