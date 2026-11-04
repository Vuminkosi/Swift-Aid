using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Geography;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class GeoRegistryEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Short_query_is_rejected_without_calling_registry()
    {
        await using var host = await TestGeoHost.StartAsync();
        using var response = await host.Client.GetAsync("/public/v1/intake/geo/search?q=a");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Service.SearchCalls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    public async Task Out_of_range_limit_is_rejected(string limit)
    {
        await using var host = await TestGeoHost.StartAsync();
        using var response = await host.Client.GetAsync($"/public/v1/intake/geo/search?q=mba&limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Service.SearchCalls);
    }

    [Fact]
    public async Task Valid_search_returns_bounded_results()
    {
        await using var host = await TestGeoHost.StartAsync();
        using var response = await host.Client.GetAsync("/public/v1/intake/geo/search?q=mba&country=SZ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var payload = JsonSerializer.Deserialize<GeoSearchResponse>(body, JsonOptions);
        Assert.NotNull(payload);
        Assert.Single(payload!.Results);
        Assert.Equal("SZ:place:mbabane", payload.Results[0].Id);
        Assert.Equal(
            ["country:SZ", "SZ:adm1:hhohho"],
            payload.Results[0].Ancestors.Select(ancestor => ancestor.Id));
        Assert.Equal(1, host.Service.SearchCalls);
    }

    [Fact]
    public async Task Missing_registry_reports_unavailable()
    {
        await using var host = await TestGeoHost.StartAsync(registerRegistry: false);
        using var response = await host.Client.GetAsync("/public/v1/intake/geo/search?q=mba");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Children_endpoint_returns_empty_for_unknown_parent()
    {
        await using var host = await TestGeoHost.StartAsync();
        using var response = await host.Client.GetAsync("/public/v1/intake/geo/children?parentId=XX%3Aadm1%3Anowhere");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var payload = JsonSerializer.Deserialize<GeoChildrenResponse>(body, JsonOptions);
        Assert.NotNull(payload);
        Assert.Empty(payload!.Children);
        Assert.Equal(1, host.Service.ChildrenCalls);
    }

    private sealed class TestGeoService : IGeographicRegistry
    {
        private static readonly GeoSearchResult Mbabane = new(
            "SZ:place:mbabane", "Mbabane", GeoLocationKind.Settlement, "SZ", "Eswatini",
            "Mbabane, Hhohho, Eswatini", null, null, true,
            [
                new GeoAdminAncestor("country:SZ", "Eswatini", GeoLocationKind.Country),
                new GeoAdminAncestor("SZ:adm1:hhohho", "Hhohho", GeoLocationKind.Region)
            ]);

        public int SearchCalls { get; private set; }
        public int ChildrenCalls { get; private set; }

        public Task<GeoSearchResponse> SearchAsync(string query, string? countryCode, int limit, CancellationToken ct)
        {
            SearchCalls++;
            return Task.FromResult(new GeoSearchResponse("test-seed", 1, [Mbabane]));
        }

        public Task<GeoChildrenResponse> GetChildrenAsync(string parentId, CancellationToken ct)
        {
            ChildrenCalls++;
            return Task.FromResult(new GeoChildrenResponse("test-seed", parentId, []));
        }
    }

    private sealed class TestGeoHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public HttpClient Client { get; }
        public TestGeoService Service { get; }

        private TestGeoHost(WebApplication app, TestGeoService service)
        {
            _app = app;
            Service = service;
            Client = app.GetTestClient();
        }

        public static async Task<TestGeoHost> StartAsync(bool registerRegistry = true)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var service = new TestGeoService();
            if (registerRegistry)
            {
                builder.Services.AddSingleton<IGeographicRegistry>(service);
            }

            var app = builder.Build();
            app.MapPublicIntakeEndpoints();
            await app.StartAsync();
            return new TestGeoHost(app, service);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
