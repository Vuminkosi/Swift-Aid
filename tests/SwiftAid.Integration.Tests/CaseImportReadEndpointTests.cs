using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Client.Intake.Imports;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class CaseImportReadEndpointTests
{
    [Fact]
    public async Task ReadsAreAuthenticatedOwnerScopedAndNonCacheable()
    {
        var reader = new Reader();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
        }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(IdentityConstants.ApplicationScheme, _ => { })
          .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton<ICaseImportWorkspaceReader>(reader);
        builder.Services.AddSingleton<ICaseImportStore>(_ => throw new InvalidOperationException("Not used by this test."));
        builder.Services.AddSingleton<CaseCsvImportService>(_ => throw new InvalidOperationException("Not used by this test."));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCaseImportEndpoints();
        await app.StartAsync();
        using var http = app.GetTestClient();
        using var anonymous = await http.GetAsync("/api/v1/case-imports/workspaces");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(reader.Actor);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var workspaces = await http.GetAsync("/api/v1/case-imports/workspaces?actorId=other");
        Assert.Equal(HttpStatusCode.OK, workspaces.StatusCode);
        Assert.True(workspaces.Headers.CacheControl?.NoStore);
        Assert.Equal("actor-1", reader.Actor);
        var client = new WorkspaceImportHttpClient(http);
        var scope = Assert.Single(await client.GetWorkspacesAsync());
        var recent = Assert.Single(await client.GetRecentAsync(scope));
        Assert.Equal(Reader.ImportId, recent.ImportId);
        Assert.Equal("org-1", reader.Organization);
        var issue = Assert.Single(await client.GetIssuesAsync(Reader.ImportId, 0));
        Assert.Equal(1, issue.RowNumber);
        using var invalid = await http.GetAsync("/api/v1/case-imports/recent?workspaceId=ws&organizationId=org&countryCode=bad");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private sealed class Reader : ICaseImportWorkspaceReader
    {
        public static Guid ImportId { get; } = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        public string? Actor { get; private set; }
        public string? Organization { get; private set; }
        public Task<IReadOnlyList<CaseImportWorkspace>> GetWorkspacesAsync(ActorContext actor, CancellationToken ct)
        {
            Actor = actor.ActorId;
            return Task.FromResult<IReadOnlyList<CaseImportWorkspace>>([new("ws-1", "org-1", "SZ")]);
        }
        public Task<IReadOnlyList<CaseImportHistoryItem>> GetRecentAsync(ActorContext actor, string workspaceId, string countryCode,
            CancellationToken ct, string? organizationId = null)
        {
            Actor = actor.ActorId;
            Organization = organizationId;
            return Task.FromResult<IReadOnlyList<CaseImportHistoryItem>>([new(ImportId, workspaceId, countryCode, "Paused", DateTimeOffset.UtcNow)]);
        }
        public Task<IReadOnlyList<CaseImportRowOutcome>> GetIssuesAsync(ActorContext actor, Guid importId, long afterRow, CancellationToken ct)
        {
            Actor = actor.ActorId;
            return Task.FromResult<IReadOnlyList<CaseImportRowOutcome>>([new(1, null, "Failed", null, "command-id")]);
        }
    }
}
