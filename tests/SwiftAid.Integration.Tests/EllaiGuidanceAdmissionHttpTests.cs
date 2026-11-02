using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiGuidanceAdmissionHttpTests
{
    [Fact]
    public async Task HttpAdmissionKeepsUnknownProposalsAndForeignSessionsNonExecutable()
    {
        var store = new InMemoryEllaiSessionStore();
        await store.CreateAsync(new("owned", EllaiMode.GuideMe, "Active", 1, 1, 1, 1, 0, 12,
            "owner", "scope", null, null, DateTimeOffset.UtcNow), default);
        await store.CreateAsync(new("foreign", EllaiMode.GuideMe, "Active", 1, 1, 1, 1, 0, 12,
            "someone-else", "scope", null, null, DateTimeOffset.UtcNow), default);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IEllaiSessionStore>(store);
        builder.Services.AddSingleton<IEllaiUtteranceOutboxStore>(store);
        builder.Services.AddSingleton(new EllaiUiRegistry());
        builder.Services.AddSingleton<IEllaiUiObservationStore, InMemoryEllaiUiObservationStore>();
        builder.Services.AddScoped<EllaiGuidanceProposalAdmission>();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "owner"),
                new Claim(SwiftAid.Application.Authorization.ActorContext.AccessEpochClaimType, "1")], "fixture"));
            return next(context);
        });
        Delegate handler = (Func<HttpContext, string, string, IEllaiSessionStore, EllaiGuidanceProposalAdmission, CancellationToken, Task<IResult>>)EllaiGuidanceProposalEndpoint.ReadAsync;
        app.MapGet("/sessions/{sessionId}/guidance/admission", handler);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/sessions/foreign/guidance/admission?proposalId=x")).StatusCode);
            using var own = await client.GetAsync("/sessions/owned/guidance/admission?proposalId=x");
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            Assert.Contains("Stale", await own.Content.ReadAsStringAsync());
            Assert.True(own.Headers.CacheControl?.NoStore);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/sessions/owned/guidance/admission?proposalId=")).StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
