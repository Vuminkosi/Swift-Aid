using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class AnalyticsEndpointTests
{
    [Theory]
    [InlineData("duplicates")]
    [InlineData("inconsistent-page")]
    [InlineData("synthetic-provenance")]
    public async Task MalformedOrSyntheticAnalyticsAreNotPresentedAsLive(string fault)
    {
        var metrics = new[] { new AnalyticsMetric("duplicate", "Fixture", 1, "cases"), new AnalyticsMetric("duplicate", "Other", 2, "cases") };
        var page = new Page<AnalyticsMetric>(fault == "duplicates" ? metrics : metrics.Take(1).ToArray(), 1, 25,
            fault == "synthetic-provenance" ? 1 : 2, false);
        var metadata = new UiCapabilityMetadata("analytics", "1", fault == "synthetic-provenance"
            ? UiCapabilityProvenance.SyntheticDevelopment : UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true);
        using var http = new HttpClient(new ResponseHandler(new(UiCapabilityResultCode.Success, page, metadata))) { BaseAddress = new Uri("https://example.test/") };
        var result = await new AnalyticsHttpClient(http).GetAsync(new(new PageRequest(1, 25)));
        Assert.Equal(UiCapabilityResultCode.Unavailable, result.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task AuthenticatedClientUsesRequestActorAndCannotExport()
    {
        var store = new AggregateStore();
        var policy = new CasePolicy();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
        }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(IdentityConstants.ApplicationScheme, _ => { })
          .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IDashboardAggregateQuery>(new DashboardAggregateQuery(store, policy, new UnitOfWork(), TimeProvider.System));
        builder.Services.AddSingleton<ICaseMonitoringQueueQuery, Monitoring>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAnalyticsEndpoints();
        await app.StartAsync();
        using var http = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/analytics/work-summary")).StatusCode);
        Assert.Null(store.ActorId);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var response = await http.GetAsync("/api/v1/analytics/work-summary?actorId=other&period=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("actor-1", store.ActorId);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/analytics/work-summary?period=invalid")).StatusCode);

        var client = new AnalyticsHttpClient(http);
        var result = await client.GetAsync(new(new PageRequest(1, 25), "90", null, "human"));
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Value!.Items.Single(item => item.Id == "new-owned-cases").Value);
        policy.AllowReadable = false;
        var revoked = await client.GetAsync(new(new PageRequest(1, 25), "90", null, "human"));
        Assert.Equal(0, revoked.Value!.Items.Single(item => item.Id == "new-owned-cases").Value);
        Assert.Equal(UiCapabilityResultCode.Unavailable, (await client.ExportPreviewAsync(new(new PageRequest()))).Code);
    }

    private sealed class AggregateStore : IDashboardAggregateStore
    {
        public string? ActorId { get; private set; }
        public Task<IReadOnlyList<DashboardAggregateCaseRow>> GetOwnedCaseRowsAsync(string ownerActorId, DateTimeOffset sinceUtc, int take, CancellationToken ct)
        {
            ActorId = ownerActorId;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult<IReadOnlyList<DashboardAggregateCaseRow>>([
                new(CasePolicy.Readable, Sector.HumanHealth, ClinicalClassification.Unclassified, CaseWorkflow.Reported, "SZ", [], now, now),
                new(CasePolicy.Denied, Sector.HumanHealth, ClinicalClassification.Unclassified, CaseWorkflow.Reported, "SZ", [], now, now)]);
        }
    }

    private sealed class CasePolicy : ICaseAccessPolicy
    {
        public static readonly Guid Readable = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        public static readonly Guid Denied = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        public bool AllowReadable { get; set; } = true;
        public ValueTask<AccessDecision> AuthorizeAsync(ActorContext actor, Guid caseId, CaseOperation operation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(caseId == Readable && AllowReadable
                ? new AccessDecision(true, AccessDecisionCode.Allowed, null)
                : new AccessDecision(false, AccessDecisionCode.GrantMissing, null));
        public ValueTask<AccessDecision> AuthorizeAllAsync(ActorContext actor, Guid caseId, IReadOnlyCollection<CaseOperation> requiredOperations, CancellationToken cancellationToken) =>
            AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, cancellationToken);
    }

    private sealed class UnitOfWork : ICaseCommandUnitOfWork
    {
        public ValueTask<ICaseCommandLease> BeginCaseAsync(ActorContext actor, Guid caseId, ImmutableArray<string> additionalActorHints, CaseLeaseMode mode, CancellationToken ct) =>
            ValueTask.FromResult<ICaseCommandLease>(new Lease(caseId));
    }

    private sealed class Lease(Guid caseId) : ICaseCommandLease
    {
        public Guid CaseId => caseId;
        public CaseLeaseMode Mode => CaseLeaseMode.Read;
        public ImmutableArray<string> LockedActorIds => [];
        public bool IsActive => true;
        public ValueTask CommitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Monitoring : ICaseMonitoringQueueQuery
    {
        public Task<MonitoringQueueResult> GetAssignedAsync(ActorContext actor, int? limit, DateTimeOffset utcNow, CancellationToken ct) =>
            Task.FromResult(new MonitoringQueueResult(CaseQueueAccess.Allowed, []));
        public Task<MonitoringQueueResult> GetOpenAsync(ActorContext actor, int? limit, DateTimeOffset utcNow, CancellationToken ct) =>
            Task.FromResult(new MonitoringQueueResult(CaseQueueAccess.Allowed, []));
    }

    private sealed class ResponseHandler(UiCapabilityResult<Page<AnalyticsMetric>> result) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) });
    }
}
