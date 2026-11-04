using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Client.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class FollowUpReviewEndpointTests
{
    [Fact]
    public async Task EndpointRequiresAuthenticationAndCookieAntiforgeryAndMapsOutcomes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
        }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(IdentityConstants.ApplicationScheme, _ => { })
          .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        var service = new ReviewService();
        builder.Services.AddSingleton<IFollowUpRecommendationReviewService>(service);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapFollowUpReviewEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var caseId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var path = $"/api/v1/cases/{caseId:D}/follow-up-recommendations/{recommendationId:D}/review";
        using var anonymous = await client.PostAsJsonAsync(path, new FollowUpReviewRequest(commandId));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var cookie = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new FollowUpReviewRequest(commandId)) };
        TestAuthenticationHandler.AddCookieAuthentication(cookie);
        using var missingToken = await client.SendAsync(cookie);
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        Assert.Equal(0, service.Calls);
        using var mixed = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new FollowUpReviewRequest(commandId)) };
        TestAuthenticationHandler.AddCookieAuthentication(mixed);
        mixed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var mixedResponse = await client.SendAsync(mixed);
        Assert.Equal(HttpStatusCode.BadRequest, mixedResponse.StatusCode);
        Assert.Equal(0, service.Calls);
        using var largeRequest = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(new string('x', 3000), System.Text.Encoding.UTF8, "application/json") };
        largeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var tooLarge = await client.SendAsync(largeRequest);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        using var streamed = new HttpRequestMessage(HttpMethod.Post, path) { Content = new UnknownLengthContent(new byte[3000]) };
        streamed.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        streamed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var streamedResponse = await client.SendAsync(streamed);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamedResponse.StatusCode);
        Assert.Equal(0, service.Calls);
        using var bearer = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new FollowUpReviewRequest(commandId)) };
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var accepted = await client.SendAsync(bearer);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(FollowUpReviewStatus.Recorded, (await accepted.Content.ReadFromJsonAsync<FollowUpReviewResult>())!.Status);
        Assert.Equal(caseId, service.CaseId);
        Assert.Equal(recommendationId, service.RecommendationId);
        Assert.Equal(commandId, service.CommandId);
        Assert.NotNull(service.Actor);
        service.Outcome = FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized;
        using var deniedRequest = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new FollowUpReviewRequest(Guid.NewGuid())) };
        deniedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var denied = await client.SendAsync(deniedRequest);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        service.Outcome = FollowUpRecommendationReviewOutcome.Stale;
        using var staleRequest = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new FollowUpReviewRequest(Guid.NewGuid())) };
        staleRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var stale = await client.SendAsync(staleRequest);
        Assert.Equal(FollowUpReviewStatus.Stale, (await stale.Content.ReadFromJsonAsync<FollowUpReviewResult>())!.Status);
    }

    [Fact]
    public async Task MobileClientPreservesCommandIdentityAndEndpointPath()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") };
        var client = new FollowUpReviewHttpClient(http);
        var caseId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        Assert.Equal(FollowUpReviewStatus.Recorded, (await client.ReviewAsync(caseId, recommendationId, commandId)).Status);
        Assert.Equal($"/api/v1/cases/{caseId:D}/follow-up-recommendations/{recommendationId:D}/review", handler.Path);
        Assert.Equal(commandId, handler.CommandId);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public Guid CommandId { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            CommandId = (await request.Content!.ReadFromJsonAsync<FollowUpReviewRequest>(cancellationToken))!.CommandId;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new FollowUpReviewResult(FollowUpReviewStatus.Recorded)) };
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":999}")]
    [InlineData("not-json")]
    public async Task MobileClientDoesNotTreatMalformedSuccessAsRecorded(string body)
    {
        using var http = new HttpClient(new StaticHandler(body)) { BaseAddress = new Uri("https://fixture.invalid/") };
        var client = new FollowUpReviewHttpClient(http);
        var result = await client.ReviewAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(FollowUpReviewStatus.Unavailable, result.Status);
    }

    private sealed class ReviewService : IFollowUpRecommendationReviewService
    {
        public int Calls { get; private set; }
        public Guid CaseId { get; private set; }
        public Guid RecommendationId { get; private set; }
        public Guid CommandId { get; private set; }
        public ActorContext? Actor { get; private set; }
        public FollowUpRecommendationReviewOutcome Outcome { get; set; } = FollowUpRecommendationReviewOutcome.Recorded;
        public Task<FollowUpRecommendationReviewOutcome> ReviewAsync(ActorContext actor, Guid caseId, Guid recommendationId, Guid commandId, CancellationToken ct)
        {
            Calls++; Actor = actor; CaseId = caseId; RecommendationId = recommendationId; CommandId = commandId;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
