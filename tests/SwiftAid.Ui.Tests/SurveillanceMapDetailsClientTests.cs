using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Geography;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Ui.Tests;

public sealed class SurveillanceMapDetailsClientTests
{
    [Theory]
    [InlineData(4, 0, 0)]
    [InlineData(5, 1, 0)]
    [InlineData(5, 0, 1)]
    [InlineData(5, 5, 1)]
    public async Task MapSuppressesSmallGroupsIncludingToggleableSectorCounts(int human, int animal, int expectedPoints)
    {
        var rows = Enumerable.Range(0, human + animal).Select(index => Row(index >= human)).ToArray();
        var reports = new Reports();
        var result = await ReadAsync(rows, reports);
        Assert.True(result.Succeeded);
        Assert.Equal(expectedPoints, result.Value!.Points.Count);
        if (expectedPoints == 1)
        {
            var point = Assert.Single(result.Value.Points);
            Assert.Equal(human, point.HumanReports);
            Assert.Equal(animal, point.AnimalReports);
            Assert.Equal(-26.5, point.Latitude);
            Assert.NotNull(point.Cases);
            Assert.Equal(rows.Select(row => row.CaseId).Order(), point.Cases.Select(row => row.CaseId).Order());
            Assert.Equal(animal, point.Cases.Count(row => row.Animal));
        }
    }

    [Fact]
    public async Task OtherWorkspacesCountriesAndExpiredRowsAreNotRead()
    {
        var reports = new Reports();
        var rows = new[] { Row(false) with { WorkspaceId = "other" }, Row(false) with { CountryCode = "ZW" },
            Row(false) with { CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-100) } };
        var result = await ReadAsync(rows, reports);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Points);
        Assert.Empty(reports.Reads);
    }

    [Fact]
    public async Task DeniedReportEvidenceCannotContributeToMarkers()
    {
        var reports = new Reports { Denied = true };
        var result = await ReadAsync(Enumerable.Range(0, 5).Select(_ => Row(false)).ToArray(), reports);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Points);
        Assert.Equal(5, result.Value.UnmappedReports);
    }

    private static CaseQueueItem Row(bool animal) => new(Guid.NewGuid(), "Open", "SZ", "actor-1", 1,
        DateTimeOffset.UtcNow, animal ? "AnimalHealth" : "HumanHealth", WorkspaceId: "selected", CreatedAtUtc: DateTimeOffset.UtcNow);

    private static async Task<UiCapabilityResult<SwiftAid.Contracts.Surveillance.SurveillanceMapDetails>> ReadAsync(CaseQueueItem[] rows, Reports reports)
    {
        await using var services = new ServiceCollection().AddSingleton<IGeographicRegistry, Registry>()
            .AddSingleton<ICaseMapCandidateStore>(new Candidates(rows)).BuildServiceProvider();
        var client = new SurveillanceMapDetailsGatewayClient(reports, new Workspace(),
            services.GetRequiredService<IServiceScopeFactory>(), new Authentication(), NullLogger<SurveillanceMapDetailsGatewayClient>.Instance);
        return await client.GetAsync("SZ", MapAggregatePeriod.Days30);
    }

    private static UiCapabilityResult<T> Success<T>(T value) => new(UiCapabilityResultCode.Success, value,
        new("test", UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true));

    private sealed class Candidates(CaseQueueItem[] rows) : ICaseMapCandidateStore
    {
        public Task<IReadOnlyList<CaseQueueCandidate>> GetAsync(string ownerActorId, string workspaceId,
            string countryCode, DateTimeOffset sinceUtc, int take, CancellationToken ct)
        {
            Assert.Equal("actor-1", ownerActorId);
            Assert.Equal("selected", workspaceId);
            Assert.Equal("SZ", countryCode);
            return Task.FromResult<IReadOnlyList<CaseQueueCandidate>>(rows.Select(row => new CaseQueueCandidate(
                row.CaseId, row.Workflow, row.CountryCode, row.AccountableOwnerActorId, row.AggregateVersion,
                row.UpdatedAtUtc, row.UpdatedAtUtc, row.Sector, row.Classification, row.WorkspaceId, row.CreatedAtUtc)).ToArray());
        }
    }

    private sealed class Reports : ICaseReportDetailsClient
    {
        public bool Denied { get; init; }
        public List<Guid> Reads { get; } = [];
        public Task<UiCapabilityResult<CaseReportDetails>> GetAsync(Guid id, CancellationToken ct = default)
        {
            Reads.Add(id);
            return Task.FromResult(Denied ? UiCapabilityResults.Unavailable<CaseReportDetails>("test") :
                Success(new CaseReportDetails(id, 1, null, [], new("SZ", "Manzini", null, "Test place", null, null, "test-place"))));
        }
    }

    private sealed class Workspace : IWorkspaceAccessClient
    {
        public Task<UiCapabilityResult<WorkspaceAccessState>> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Success(new WorkspaceAccessState([new("selected", "Selected")], "selected")));
        public Task<UiCapabilityResult<WorkspaceAccessState>> SelectWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Authentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "actor-1"), new(ActorContext.AccessEpochClaimType, "3")], "test"))));
    }

    private sealed class Registry : IGeographicRegistry
    {
        public Task<GeoSearchResponse> SearchAsync(string query, string? countryCode, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new GeoSearchResponse("test", 1, [new("test-place", "Test place", GeoLocationKind.Settlement, "SZ", "Eswatini", "Manzini", -26.5, 31.5, false, [new("manzini", "Manzini", GeoLocationKind.Region)])]));
        public Task<GeoChildrenResponse> GetChildrenAsync(string parentId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
