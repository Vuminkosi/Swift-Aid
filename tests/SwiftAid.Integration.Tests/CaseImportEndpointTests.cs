using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Client.Intake.Imports;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Gateway.Ingestion;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.IntakeImports;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class CaseImportEndpointTests
{
    [IntakePostgresFact]
    public async Task MobileImportClientUsesRealEndpointAndRecoversDurableResult()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        await using var host = await Host.StartAsync(fixture);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-import-token");
        var client = new WorkspaceImportHttpClient(host.Client);
        var id = Guid.NewGuid();
        Assert.Null(await client.GetAsync(id));
        var serverContext = CaseImportPostgresTests.Context;
        var scope = Assert.Single(await client.GetWorkspacesAsync(), item =>
            item.WorkspaceId == serverContext.WorkspaceId && item.CountryCode == "ZW");
        var context = new WorkspaceImportContext(serverContext.WorkspaceId, serverContext.Confirmations, serverContext.OrganizationId);
        using var reader = new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid()));
        SwiftAid.Contracts.Intake.Imports.CaseCsvBatch? batch = null;
        await foreach (var item in SwiftAid.Contracts.Intake.Imports.CaseCsvBatchReader.ReadAsync(reader)) batch = item;
        Assert.NotNull(batch);
        var result = await client.ImportAsync(id, context, batch);
        Assert.Equal("Accepted", Assert.Single(result.Rows).Status);
        Assert.Equal("Completed", result.Summary!.State);
        Assert.Equal(2, (await client.GetAsync(id))!.NextRow);
        Assert.True(await client.MatchesContextAsync(id, context));
        Assert.False(await client.MatchesContextAsync(id, context with { FileSha256 = new string('a', 64) }));
        Assert.Contains(await client.GetRecentAsync(scope), item => item.ImportId == id);
        Assert.Empty(await client.GetIssuesAsync(id, 0));
        var replay = await client.ImportAsync(id, context, batch);
        Assert.Equal("Replayed", Assert.Single(replay.Rows).Status);
        Assert.Equal(1, replay.Summary!.Accepted);
        await fixture.Db.ActorAccessStates.Where(row => row.ActorId == "officer-1")
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AccessEpoch, 8));
        var denied = await Assert.ThrowsAsync<WorkspaceImportException>(() => client.GetRecentAsync(scope));
        Assert.Equal("not-authorized", denied.Code);
        var contextDenied = await Assert.ThrowsAsync<WorkspaceImportException>(() => client.MatchesContextAsync(id, context));
        Assert.Equal("not-authorized", contextDenied.Code);
    }

    [IntakePostgresFact]
    public async Task MobileImportClientContinuesFromDurableCheckpoint()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        await using var host = await Host.StartAsync(fixture);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-import-token");
        var client = new WorkspaceImportHttpClient(host.Client);
        var id = Guid.NewGuid();
        var serverContext = CaseImportPostgresTests.Context;
        var context = new WorkspaceImportContext(serverContext.WorkspaceId, serverContext.Confirmations, serverContext.OrganizationId, new string('a', 64));
        var first = new SwiftAid.Contracts.Intake.Imports.CaseCsvBatch(1, 1, false, CaseImportPostgresTests.Csv(Guid.NewGuid()));
        var second = new SwiftAid.Contracts.Intake.Imports.CaseCsvBatch(2, 1, true, CaseImportPostgresTests.Csv(Guid.NewGuid()));
        var initial = await client.ImportAsync(id, context, first);
        Assert.Equal("Paused", initial.Summary!.State);
        var checkpoint = (await client.GetAsync(id))!;
        Assert.Equal(2, checkpoint.NextRow);
        Assert.True(await client.MatchesContextAsync(id, context));
        Assert.False(await client.MatchesContextAsync(id, context with { FileSha256 = new string('b', 64) }));
        Assert.False(await client.MatchesContextAsync(id, context with { FileSha256 = null }));
        Assert.Null(await fixture.Store.MatchesContextAsync(CaseRegistrationBackendTests.CreateOfficer("another-actor"),
            id, serverContext with { FileSha256 = new string('a', 64) }, default));
        Assert.Equal(checkpoint, await client.GetAsync(id));
        var mismatch = await Assert.ThrowsAsync<WorkspaceImportException>(() => client.ImportAsync(id,
            context with { FileSha256 = new string('b', 64) }, second));
        Assert.Equal("import-context-conflict", mismatch.Code);
        Assert.Equal(2, (await client.GetAsync(id))!.NextRow);
        var completed = await client.ImportAsync(id, context, second);
        Assert.Equal("Completed", completed.Summary!.State);
        Assert.Equal(3, completed.Summary.NextRow);
        Assert.Equal(2, completed.Summary.Accepted);
    }

    [IntakePostgresFact]
    public async Task WebStartedFingerprintCanBeVerifiedAndContinuedByMobile()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        await using var host = await Host.StartAsync(fixture);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-import-token");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "officer-1"), new Claim(ActorContext.AccessEpochClaimType, "7")], "fixture"));
        var web = new WorkspaceImportClient(new CommunityFixedAuthenticationStateProvider(principal),
            host.Services.GetRequiredService<IServiceScopeFactory>());
        var mobile = new WorkspaceImportHttpClient(host.Client);
        var source = CaseImportPostgresTests.Context with { FileSha256 = new string('c', 64) };
        var context = new WorkspaceImportContext(source.WorkspaceId, source.Confirmations, source.OrganizationId, source.FileSha256);
        var id = Guid.NewGuid();
        var initial = await web.ImportAsync(id, source,
            new CaseCsvBatch(1, 1, false, CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
        Assert.Equal("Paused", initial.Summary!.State);
        Assert.True(await mobile.MatchesContextAsync(id, context));
        Assert.True(await web.MatchesContextAsync(id, source, default));
        var completed = await mobile.ImportAsync(id, context, new SwiftAid.Contracts.Intake.Imports.CaseCsvBatch(
            2, 1, true, CaseImportPostgresTests.Csv(Guid.NewGuid())));
        Assert.Equal("Completed", completed.Summary!.State);
        Assert.Equal(2, completed.Summary.Accepted);
    }

    [IntakePostgresFact]
    public async Task MobileStartedFingerprintCanBeVerifiedAndContinuedByWeb()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        await using var host = await Host.StartAsync(fixture);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-import-token");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "officer-1"), new Claim(ActorContext.AccessEpochClaimType, "7")], "fixture"));
        var web = new WorkspaceImportClient(new CommunityFixedAuthenticationStateProvider(principal),
            host.Services.GetRequiredService<IServiceScopeFactory>());
        var mobile = new WorkspaceImportHttpClient(host.Client);
        var source = CaseImportPostgresTests.Context with { FileSha256 = new string('d', 64) };
        var context = new WorkspaceImportContext(source.WorkspaceId, source.Confirmations, source.OrganizationId, source.FileSha256);
        var id = Guid.NewGuid();
        var initial = await mobile.ImportAsync(id, context, new SwiftAid.Contracts.Intake.Imports.CaseCsvBatch(
            1, 1, false, CaseImportPostgresTests.Csv(Guid.NewGuid())));
        Assert.Equal("Paused", initial.Summary!.State);
        Assert.True(await web.MatchesContextAsync(id, source, default));
        var completed = await web.ImportAsync(id, source,
            new CaseCsvBatch(2, 1, true, CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
        Assert.Equal("Completed", completed.Summary!.State);
        Assert.Equal(2, (await mobile.GetAsync(id))!.Accepted);
    }

    [IntakePostgresFact]
    public async Task Real_http_import_requires_authentication_and_csrf_and_restores_durable_status()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        await using var host = await Host.StartAsync(fixture);
        var id = Guid.NewGuid();
        var command = Guid.NewGuid();
        using var anonymous = await host.Client.SendAsync(Request(id, command));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var cookieRequest = Request(id, command);
        cookieRequest.Headers.Add("Cookie", "swiftaid-import-test=authenticated");
        using var csrf = await host.Client.SendAsync(cookieRequest);
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        using var contextCheck = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/case-imports/{id:D}/context-check")
        {
            Content = System.Net.Http.Json.JsonContent.Create(CaseImportPostgresTests.Context)
        };
        contextCheck.Headers.Add("Cookie", "swiftaid-import-test=authenticated");
        using var rejectedContextCheck = await host.Client.SendAsync(contextCheck);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedContextCheck.StatusCode);
        using var authorized = Request(id, command);
        authorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-import-token");
        using var accepted = await host.Client.SendAsync(authorized);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var first = JsonSerializer.Deserialize<CaseImportBatchResult>(await accepted.Content.ReadAsStringAsync(), Host.Json)!;
        Assert.Equal("Accepted", Assert.Single(first.Rows).Status);
        using var query = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/case-imports/{id:D}");
        query.Headers.Authorization = authorized.Headers.Authorization;
        using var restored = await host.Client.SendAsync(query);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var summary = JsonSerializer.Deserialize<CaseImportSummary>(await restored.Content.ReadAsStringAsync(), Host.Json)!;
        Assert.Equal("Completed", summary.State);
        Assert.Equal(1, summary.Accepted);
        using var retry = Request(id, command);
        retry.Headers.Authorization = authorized.Headers.Authorization;
        using var replay = await host.Client.SendAsync(retry);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var second = JsonSerializer.Deserialize<CaseImportBatchResult>(await replay.Content.ReadAsStringAsync(), Host.Json)!;
        Assert.Equal("Replayed", Assert.Single(second.Rows).Status);
        Assert.Equal(first.Rows[0].CaseId, second.Rows[0].CaseId);
    }

    internal static HttpRequestMessage Request(Guid id, Guid command) => Host.Request(id, 1, true,
        CaseImportPostgresTests.Context, CaseImportPostgresTests.Csv(command));

    [IntakePostgresFact]
    public async Task Chunked_body_is_bounded_and_keeps_earlier_commits_recoverable()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        await using var host = await Host.StartAsync(fixture);
        var id = Guid.NewGuid();
        var command = Guid.NewGuid();
        var valid = CaseImportPostgresTests.Csv(command);
        var invalid = CaseImportPostgresTests.Csv(Guid.NewGuid()).Split('\n', 2)[1]
            .Replace("Original supplied concern", new string('x', 15000));
        var body = valid + string.Concat(Enumerable.Repeat(invalid, 600));
        using var request = Request(id, command);
        request.Content?.Dispose();
        request.Content = new ChunkedCsvContent(Encoding.UTF8.GetBytes(body));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-import-token");
        Assert.Null(request.Content.Headers.ContentLength);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var result = JsonSerializer.Deserialize<CaseImportBatchResult>(await response.Content.ReadAsStringAsync(), Host.Json)!;
        Assert.Equal("batch-byte-limit", result.ErrorCode);
        Assert.Equal("Paused", result.Summary!.State);
        Assert.Equal(1, result.Summary.Accepted);
        Assert.InRange(result.Summary.NextRow, 2, 600);
        Assert.Equal(1, await fixture.Db.CommandReceipts.CountAsync(row => row.CommandId == command));
    }

    private sealed class ChunkedCsvContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }

    internal sealed class Host(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public HttpClient Client { get; } = client;
        public IServiceProvider Services => app.Services;

        public static async Task<Host> StartAsync(CaseImportPostgresTests.Fixture fixture, SwiftAid.Application.Intake.IIntakeSubmissionValidator? validator = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            builder.Services.AddAntiforgery();
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            }).AddScheme<AuthenticationSchemeOptions, ImportAuthentication>(IdentityConstants.ApplicationScheme, _ => { })
              .AddScheme<AuthenticationSchemeOptions, ImportAuthentication>(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddScoped(_ => new SwiftAidDbContext(new DbContextOptionsBuilder<SwiftAidDbContext>()
                .UseNpgsql(fixture.Db.Database.GetConnectionString()).UseOpenIddict().Options));
            builder.Services.AddScoped<ICaseImportStore>(services => new PostgresCaseImportStore(
                services.GetRequiredService<SwiftAidDbContext>(), fixture.Protection, TimeProvider.System));
            builder.Services.AddScoped<ICaseImportWorkspaceReader>(services =>
                (PostgresCaseImportStore)services.GetRequiredService<ICaseImportStore>());
            builder.Services.AddScoped(services => fixture.CreateService(services.GetRequiredService<SwiftAidDbContext>(), services.GetRequiredService<ICaseImportStore>(), validator));
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapCaseImportEndpoints();
            await app.StartAsync();
            return new(app, app.GetTestClient());
        }

        public static HttpRequestMessage Request(Guid id, long start, bool final, CaseImportContext context, string csv)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/case-imports/{id:D}/rows?startRow={start}&finalBatch={final.ToString().ToLowerInvariant()}");
            request.Headers.Add("X-SwiftAid-Import", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(context, Json)));
            request.Content = new StringContent(csv, Encoding.UTF8, "text/csv");
            return request;
        }

        public async ValueTask DisposeAsync() { Client.Dispose(); await app.DisposeAsync(); }
    }

    private sealed class ImportAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var authorized = Scheme.Name == IdentityConstants.ApplicationScheme
                ? Request.Cookies["swiftaid-import-test"] == "authenticated"
                : Request.Headers.Authorization == "Bearer synthetic-import-token";
            return Task.FromResult(authorized
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "officer-1"), new Claim(ActorContext.AccessEpochClaimType, "7")], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
        }
    }
}
