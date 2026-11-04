using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Cases;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Ui.Tests;

public sealed class DashboardAggregateClientTests
{
    [Fact]
    public async Task DashboardAndMapConcurrentReadsUseSeparateDisposableScopes()
    {
        var gate = new AggregateReadGate();
        await using var services = new ServiceCollection()
            .AddSingleton(gate)
            .AddScoped<IsolatedAggregateQuery>()
            .AddScoped<IDashboardAggregateQuery>(sp => sp.GetRequiredService<IsolatedAggregateQuery>())
            .AddScoped<IMapAggregateQuery>(sp => sp.GetRequiredService<IsolatedAggregateQuery>())
            .BuildServiceProvider();
        await using var circuit = services.CreateAsyncScope();
        var dashboard = new DashboardOverviewGatewayClient(new SignedInAuthStateProvider(),
            circuit.ServiceProvider, new ThrowingMetadataProvider());
        var map = new MapAggregateGatewayClient(new SignedInAuthStateProvider(), circuit.ServiceProvider);
        var overview = dashboard.GetAsync(new DashboardOverviewQuery());
        var areas = map.GetAsync(new MapAggregateQuery(new PageRequest(1, 25)));
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        await Task.WhenAll(overview, areas);
        Assert.Equal(UiCapabilityResultCode.Success, (await overview).Code);
        Assert.Equal(UiCapabilityResultCode.Success, (await areas).Code);
        Assert.Equal(2, gate.Instances.Count);
        Assert.Equal(2, gate.Disposed.Count);
    }

    private sealed class AggregateReadGate
    {
        public HashSet<Guid> Instances { get; } = [];
        public HashSet<Guid> Disposed { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
    }

    private sealed class IsolatedAggregateQuery(AggregateReadGate gate)
        : IDashboardAggregateQuery, IMapAggregateQuery, IAsyncDisposable
    {
        private readonly Guid _id = Guid.NewGuid();
        private async Task ReadAsync(CancellationToken ct)
        {
            lock (gate)
            {
                gate.Instances.Add(_id);
                if (++gate.Calls == 2) gate.Started.TrySetResult();
            }
            await gate.Release.Task.WaitAsync(ct);
        }
        public async Task<DashboardAggregateResult> GetAsync(ActorContext actor, TimeSpan window, CancellationToken ct)
        {
            await ReadAsync(ct);
            return new(DashboardAggregateAccess.Allowed, [], DateTimeOffset.UtcNow, false);
        }
        public async Task<SwiftAid.Contracts.Surveillance.SurveillanceAggregateResult> GetAsync(
            ActorContext actor, string? regionCode, TimeSpan window, CancellationToken ct)
        {
            await ReadAsync(ct);
            return new(SwiftAid.Contracts.Surveillance.SurveillanceAggregateAccess.Allowed, [], DateTimeOffset.UtcNow, false);
        }
        public ValueTask DisposeAsync()
        {
            lock (gate) gate.Disposed.Add(_id);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task DashboardClient_RejectsInvalidQueryBeforeResolvingServices()
    {
        var client = new DashboardOverviewGatewayClient(
            new SignedOutAuthStateProvider(),
            new ServiceCollection().BuildServiceProvider(),
            new ThrowingMetadataProvider());

        var invalid = await client.GetAsync(new DashboardOverviewQuery(
            Period: (DashboardOverviewPeriod)123));

        Assert.Equal(UiCapabilityResultCode.InvalidRequest, invalid.Code);
        Assert.Null(invalid.Value);
        Assert.False(invalid.Metadata.IsAvailable);
    }

    [Fact]
    public async Task DashboardClient_DeniesSignedOutCallers()
    {
        var client = new DashboardOverviewGatewayClient(
            new SignedOutAuthStateProvider(),
            new ServiceCollection().BuildServiceProvider(),
            new ThrowingMetadataProvider());

        var denied = await client.GetAsync(new DashboardOverviewQuery());

        Assert.Equal(UiCapabilityResultCode.Denied, denied.Code);
    }

    [Fact]
    public async Task DashboardClients_ObserveCancellationBeforeReturningResults()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var dashboard = new DashboardOverviewGatewayClient(
            new SignedInAuthStateProvider(),
            new ServiceCollection().BuildServiceProvider(),
            new ThrowingMetadataProvider());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await dashboard.GetAsync(new DashboardOverviewQuery(), cancellation.Token));

        var analytics = new AnalyticsGatewayClient(
            new SignedInAuthStateProvider(),
            new ServiceCollection().BuildServiceProvider());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await analytics.GetAsync(
                new AnalyticsQuery(new PageRequest(1, 25)), cancellation.Token));
    }

    [Fact]
    public async Task DashboardClient_ProjectsValidOverviewFromReadableRows()
    {
        var asOf = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var services = new ServiceCollection();
        services.AddSingleton<IDashboardAggregateQuery>(
            new StubDashboardAggregateQuery(new DashboardAggregateResult(
                DashboardAggregateAccess.Allowed,
                new[]
                {
                    new DashboardAggregateCaseRow(
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        Sector.HumanHealth,
                        ClinicalClassification.Confirmed,
                        CaseWorkflow.Active,
                        "ZW",
                        new[] { "ZW-HA" },
                        asOf.AddDays(-2),
                        asOf.AddDays(-1))
                },
                asOf,
                false)));
        var client = new DashboardOverviewGatewayClient(
            new SignedInAuthStateProvider(),
            services.BuildServiceProvider(),
            new StubMetadataProvider());

        var result = await client.GetAsync(new DashboardOverviewQuery());

        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        Assert.NotNull(result.Value);
        Assert.True(result.Value.IsValid);
        Assert.Equal(4, result.Value.Kpis.Count);
        Assert.Single(result.Value.RecentCases);
    }

    [Fact]
    public async Task AnalyticsClient_ProjectsScopedMetricsAndKeepsExportsGated()
    {
        var asOf = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var services = new ServiceCollection();
        services.AddSingleton<IDashboardAggregateQuery>(
            new StubDashboardAggregateQuery(new DashboardAggregateResult(
                DashboardAggregateAccess.Allowed,
                new[]
                {
                    new DashboardAggregateCaseRow(
                        Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        Sector.HumanHealth,
                        ClinicalClassification.Suspected,
                        CaseWorkflow.Reported,
                        "ZW",
                        Array.Empty<string>(),
                        asOf.AddDays(-1),
                        asOf)
                },
                asOf,
                false)));
        services.AddSingleton<ICaseMonitoringQueueQuery>(new StubMonitoringQueueQuery());
        var client = new AnalyticsGatewayClient(
            new SignedInAuthStateProvider(),
            services.BuildServiceProvider());

        var metrics = await client.GetAsync(new AnalyticsQuery(new PageRequest(1, 25)));
        var invalid = await client.GetAsync(new AnalyticsQuery(new PageRequest(1, 25), Period: "9"));
        var denied = await new AnalyticsGatewayClient(
            new SignedOutAuthStateProvider(),
            services.BuildServiceProvider()).GetAsync(new AnalyticsQuery(new PageRequest(1, 25)));
        var export = await client.ExportPreviewAsync(new AnalyticsQuery(new PageRequest(1, 25)));

        Assert.Equal(UiCapabilityResultCode.Success, metrics.Code);
        Assert.NotNull(metrics.Value);
        Assert.NotEmpty(metrics.Value.Items);
        Assert.All(metrics.Value.Items, metric =>
        {
            Assert.False(string.IsNullOrWhiteSpace(metric.Label));
            Assert.False(string.IsNullOrWhiteSpace(metric.Unit));
            Assert.True(metric.Value >= 0);
        });
        Assert.Equal(UiCapabilityResultCode.InvalidRequest, invalid.Code);
        Assert.Equal(UiCapabilityResultCode.Denied, denied.Code);
        Assert.Equal(UiCapabilityResultCode.Unavailable, export.Code);
    }

    [Fact]
    public async Task SummaryClient_ProjectsValidSummaryAndDeniesSignedOut()
    {
        var caseId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var created = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var services = new ServiceCollection();
        services.AddSingleton<ICaseSummaryQuery>(
            new StubSummaryQuery(new CaseSummaryResult(
                CaseSummaryAccess.Allowed,
                new CaseSummaryData(
                    caseId,
                    CaseKind.Human,
                    Sector.HumanHealth,
                    ClinicalClassification.Confirmed,
                    CaseWorkflow.Active,
                    "ZW",
                    new[] { "ZW-HA" },
                    "actor-1",
                    "Officer One",
                    created,
                    created))));
        var client = new CaseDetailSummaryGatewayClient(
            new SignedInAuthStateProvider(),
            services.BuildServiceProvider(),
            new StubMetadataProvider());

        var result = await client.GetAsync(caseId);
        var invalid = await client.GetAsync(Guid.Empty);
        var denied = await new CaseDetailSummaryGatewayClient(
            new SignedOutAuthStateProvider(),
            services.BuildServiceProvider(),
            new StubMetadataProvider()).GetAsync(caseId);

        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        Assert.NotNull(result.Value);
        Assert.True(result.Value.IsValid);
        Assert.Equal(caseId, result.Value.CaseId);
        Assert.Equal(UiCapabilityResultCode.InvalidRequest, invalid.Code);
        Assert.Equal(UiCapabilityResultCode.Denied, denied.Code);
    }

    private sealed class SignedOutAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal()));
    }

    private sealed class SignedInAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "actor-1"),
                        new Claim(ActorContext.AccessEpochClaimType, "3")
                    ],
                    "probe"))));
    }

    private sealed class StubDashboardAggregateQuery(DashboardAggregateResult result) : IDashboardAggregateQuery
    {
        public Task<DashboardAggregateResult> GetAsync(
            ActorContext actor, TimeSpan window, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class StubSummaryQuery(CaseSummaryResult result) : ICaseSummaryQuery
    {
        public Task<CaseSummaryResult> GetAsync(
            ActorContext actor, Guid caseId, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class StubMonitoringQueueQuery : ICaseMonitoringQueueQuery
    {
        public Task<MonitoringQueueResult> GetAssignedAsync(
            ActorContext actor, int? limit, DateTimeOffset utcNow, CancellationToken ct) =>
            Task.FromResult(new MonitoringQueueResult(CaseQueueAccess.Allowed, []));

        public Task<MonitoringQueueResult> GetOpenAsync(
            ActorContext actor, int? limit, DateTimeOffset utcNow, CancellationToken ct) =>
            Task.FromResult(new MonitoringQueueResult(CaseQueueAccess.Allowed, []));
    }

    private sealed class StubMetadataProvider : IIntakePublicMetadataProvider
    {
        public PublicIntakeMetadata GetMetadata() =>
            new("test", "test", "test", "test", "test");

        public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken ct) =>
            Task.FromResult(new PublicIntakeCountryCatalogue(
                "test",
                "test",
                new[] { new PublicIntakeCountry("ZW", "Zimbabwe") }));
    }

    private sealed class ThrowingMetadataProvider : IIntakePublicMetadataProvider
    {
        public PublicIntakeMetadata GetMetadata() => throw new InvalidOperationException("probe failure");

        public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken ct) =>
            throw new InvalidOperationException("probe failure");
    }
}
