using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class HealthOfficerDesignationEndpointTests
{
    [Fact]
    public async Task EndpointUsesAuthenticatedActorAndRejectsUnsafeRequests()
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
        var service = new DesignationService();
        builder.Services.AddSingleton<IHealthOfficerDesignationAdministration>(service);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthOfficerDesignationEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        const string path = "/api/v1/health-officer-designations/changes";
        var requestBody = new HealthOfficerDesignationChangeRequest
        {
            CommandId = Guid.NewGuid(), TargetActorId = "target", OrganizationId = "org", CountryCode = "SZ",
            Sector = "HumanHealth", Status = "Active", ValidFromUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1), ExpectedVersion = 0
        };
        var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using (var anonymous = await client.PostAsJsonAsync(path, requestBody)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var cookie = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(requestBody) })
        {
            TestAuthenticationHandler.AddCookieAuthentication(cookie);
            using var response = await client.SendAsync(cookie);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var mixed = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(requestBody) })
        {
            TestAuthenticationHandler.AddCookieAuthentication(mixed);
            mixed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
            using var response = await client.SendAsync(mixed);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        async Task<HttpResponseMessage> Send(string body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
            return await client.SendAsync(request);
        }
        foreach (var invalid in new[] { "{}", json[..^1] + ",\"actorId\":\"forged\"}", json[..^1] + ",\"COMMANDID\":\"" + Guid.NewGuid() + "\"}", "not-json" })
        {
            using var response = await Send(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var response = await Send(new string('x', 3000))) Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using (var streamed = new HttpRequestMessage(HttpMethod.Post, path) { Content = new UnknownLengthBody() })
        {
            streamed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
            using var response = await client.SendAsync(streamed);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        Assert.Equal(0, service.Calls);
        using (var response = await Send(json))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var receipt = await response.Content.ReadFromJsonAsync<HealthOfficerDesignationChangeReceipt>();
            Assert.Equal(service.Id, receipt!.DesignationId);
            Assert.False(receipt.Replayed);
        }
        Assert.NotNull(service.Actor);
        Assert.Equal(requestBody.CommandId, service.Change!.CommandId);
        Assert.Equal("target", service.Change.TargetActorId);
        service.Status = HealthOfficerDesignationChangeStatus.Replayed;
        using (var response = await Send(json)) Assert.True((await response.Content.ReadFromJsonAsync<HealthOfficerDesignationChangeReceipt>())!.Replayed);
        service.Status = HealthOfficerDesignationChangeStatus.Denied;
        using (var response = await Send(json)) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        service.Status = HealthOfficerDesignationChangeStatus.Conflict;
        using (var response = await Send(json)) Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        service.Status = HealthOfficerDesignationChangeStatus.Unavailable;
        using (var response = await Send(json)) Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed class DesignationService : IHealthOfficerDesignationAdministration
    {
        public int Calls { get; private set; }
        public ActorContext? Actor { get; private set; }
        public HealthOfficerDesignationChange? Change { get; private set; }
        public Guid Id { get; } = Guid.NewGuid();
        public HealthOfficerDesignationChangeStatus Status { get; set; } = HealthOfficerDesignationChangeStatus.Recorded;
        public Task<HealthOfficerDesignationChangeResult> ChangeAsync(ActorContext actor, HealthOfficerDesignationChange change, CancellationToken ct)
        {
            Calls++; Actor = actor; Change = change;
            return Task.FromResult(new HealthOfficerDesignationChangeResult(Status, Id, 1));
        }
    }

    private sealed class UnknownLengthBody : HttpContent
    {
        public UnknownLengthBody() => Headers.ContentType = new MediaTypeHeaderValue("application/json");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[3000]).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
