using System.Net;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Integration.Tests;

public sealed class CaseReportDetailsEndpointTests
{
    [Theory]
    [InlineData(false, true, false, HttpStatusCode.Unauthorized)]
    [InlineData(true, false, false, HttpStatusCode.NotFound)]
    [InlineData(true, true, true, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, true, false, HttpStatusCode.OK)]
    public async Task ReportEndpointPreservesAuthorizationPrivacyAndFailures(bool authenticated, bool allowed, bool fail, HttpStatusCode expected)
    {
        var query = new Query(allowed, fail);
        await using var host = await TestCorrectionReadHost.StartAsync(
            new(CaseCorrectionReadAccess.NotFoundOrNotAuthorized, null), reportQuery: query, registerBearer: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cases/{Guid.NewGuid()}/report-details");
        if (authenticated) TestAuthenticationHandler.AddCookieAuthentication(request);
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(authenticated ? 1 : 0, query.Calls);
        Assert.DoesNotContain("latitude", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("longitude", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("internal failure", body);
        if (authenticated) Assert.True(response.Headers.CacheControl?.NoStore);
        if (expected == HttpStatusCode.OK) Assert.Contains("Example clinic", body);
        else Assert.DoesNotContain("Example clinic", body);
    }

    private sealed class Query(bool allowed, bool fail) : ICaseReportReadQuery
    {
        public int Calls { get; private set; }
        public Task<CaseReportDetails?> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct)
        {
            Calls++;
            if (fail) throw new InvalidOperationException("internal failure");
            return Task.FromResult<CaseReportDetails?>(allowed ? new(caseId, 1, null, [], null,
                new(new("Example clinic", "https://www.openstreetmap.org/node/1", 2), null, true, 500)) : null);
        }
    }
}
