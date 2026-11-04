using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Gateway.Presentation;
using SwiftAid.Infrastructure.Persistence.Repositories.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Integration.Tests;

public sealed class PublicIntakeProcessingPolicyEndpointTests
{
    [Fact]
    public async Task AnonymousReadReturnsOnlyCurrentPolicyWithoutCaching()
    {
        await using var app = await StartAsync(new PolicyClient(new("/privacy#privacy-use-heading", "v1"), true));
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/public/v1/intake/processing-policy");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var policy = await response.Content.ReadFromJsonAsync<PublicReportProcessingPolicy>();
        Assert.Equal("/privacy#privacy-use-heading", policy?.Reference);
        Assert.Equal("v1", policy?.Version);
    }

    [Fact]
    public async Task MobileDescriptorMatchesTheAuthoritativeConsentValidator()
    {
        var options = Options.Create(new IntakeConsentOptions
        {
            ProcessingPolicyReference = "/privacy#privacy-use-heading", ProcessingPolicyVersion = "v1",
            RecordingPolicyReference = "/privacy#privacy-sensitive-heading", RecordingPolicyVersion = "v1"
        });
        await using var app = await StartAsync(new GatewayPublicIntakeProcessingPolicyClient(options));
        using var http = app.GetTestClient();
        var result = await new HttpPublicIntakeProcessingPolicyClient(http).GetPolicyAsync();
        Assert.True(result.Succeeded);
        var policy = result.Value!;
        var confirmation = new IntakeConfirmations(true, "SZ", "fixture", true, true, policy.Reference, policy.Version, null, null, null);
        var validator = new ConfiguredIntakeConsentPolicy(options);
        Assert.True(await validator.IsCurrentAsync(confirmation, false, default));
        Assert.False(await validator.IsCurrentAsync(confirmation with { ProcessingPolicyVersion = "old" }, false, default));
        Assert.False(await validator.IsCurrentAsync(confirmation with { ProcessingConfirmed = false }, false, default));
    }

    [Fact]
    public async Task IncompleteConsentConfigurationIsNotAdvertisedAsAvailable()
    {
        var options = Options.Create(new IntakeConsentOptions
        {
            ProcessingPolicyReference = "/privacy", ProcessingPolicyVersion = "v1"
        });
        await using var app = await StartAsync(new GatewayPublicIntakeProcessingPolicyClient(options));
        using var response = await app.GetTestClient().GetAsync("/public/v1/intake/processing-policy");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData(false, "/privacy", "v1")]
    [InlineData(true, "//other.test/privacy", "v1")]
    public async Task UnavailableOrInvalidPolicyIsNotPublished(bool available, string reference, string version)
    {
        await using var app = await StartAsync(new PolicyClient(new(reference, version), available));
        using var response = await app.GetTestClient().GetAsync("/public/v1/intake/processing-policy");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static async Task<WebApplication> StartAsync(IPublicIntakeProcessingPolicyClient client)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(client);
        var app = builder.Build();
        app.UseAuthorization();
        app.MapPublicIntakeEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class PolicyClient(PublicReportProcessingPolicy policy, bool available) : IPublicIntakeProcessingPolicyClient
    {
        public Task<UiCapabilityResult<PublicReportProcessingPolicy>> GetPolicyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new UiCapabilityResult<PublicReportProcessingPolicy>(UiCapabilityResultCode.Success, policy,
                new("public-intake-processing-policy", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, available)));
    }
}
