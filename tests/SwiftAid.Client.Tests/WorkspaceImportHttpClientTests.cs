using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SwiftAid.Client.Intake.Imports;
using SwiftAid.Contracts.Intake;
using SwiftAid.Contracts.Intake.Imports;

namespace SwiftAid.Client.Tests;

public sealed class WorkspaceImportHttpClientTests
{
    [Fact]
    public async Task SendsBoundedBatchWithConfirmedScopeAndReadsDurableCheckpoint()
    {
        var id = Guid.NewGuid();
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/api/v1/case-imports/{id:D}/rows?startRow=101&finalBatch=true", request.RequestUri!.PathAndQuery);
            Assert.Equal("text/csv", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("header\nrow\n", await request.Content.ReadAsStringAsync());
            var metadata = Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.GetValues("X-SwiftAid-Import").Single()));
            using var document = JsonDocument.Parse(metadata);
            Assert.Equal("workspace-1", document.RootElement.GetProperty("workspaceId").GetString());
            Assert.Equal("org-1", document.RootElement.GetProperty("organizationId").GetString());
            var summary = new WorkspaceImportSummary(id, "Completed", 102, 1, 0, 0, 0, null, DateTimeOffset.UtcNow, "org-1");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new WorkspaceImportBatchResult(id,
                [new(101, Guid.NewGuid(), "Accepted", Guid.NewGuid(), null)], summary, null)) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://gateway.example/") };
        var client = new WorkspaceImportHttpClient(http);
        var context = new WorkspaceImportContext("workspace-1", new IntakeConfirmations(true, "SZ", "1", true, true, "/privacy", "1", null, null, null), "org-1");
        var result = await client.ImportAsync(id, context, new CaseCsvBatch(101, 1, true, "header\nrow\n"));
        Assert.Equal(102, result.Summary!.NextRow);
        Assert.Equal("Completed", result.Summary.State);
    }

    [Fact]
    public async Task PreservesConflictCodeWithoutTreatingItAsCompleted()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new { code = "row-content-conflict" })
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://gateway.example/") };
        var client = new WorkspaceImportHttpClient(http);
        var context = new WorkspaceImportContext("workspace", new IntakeConfirmations(true, "SZ", "1", true, true, "/privacy", "1", null, null, null), "org");
        var error = await Assert.ThrowsAsync<WorkspaceImportException>(() => client.ImportAsync(Guid.NewGuid(), context,
            new CaseCsvBatch(1, 1, false, "header\nrow\n")));
        Assert.Equal("row-content-conflict", error.Code);
    }

    [Fact]
    public async Task RejectsSuccessWithoutACompleteMatchingCheckpoint()
    {
        var id = Guid.NewGuid();
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new WorkspaceImportBatchResult(id, [],
                new WorkspaceImportSummary(id, "Completed", 1, 0, 0, 0, 0, null, DateTimeOffset.UtcNow), null))
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://gateway.example/") };
        var client = new WorkspaceImportHttpClient(http);
        var context = new WorkspaceImportContext("workspace", new IntakeConfirmations(true, "SZ", "1", true, true, "/privacy", "1", null, null, null), "org");
        var error = await Assert.ThrowsAsync<WorkspaceImportException>(() => client.ImportAsync(id, context,
            new CaseCsvBatch(1, 1, true, "header\nrow\n")));
        Assert.Equal("invalid-response", error.Code);
    }

    [Fact]
    public async Task ContextCheckSendsFingerprintAndRequiresExplicitMatchField()
    {
        var id = Guid.NewGuid();
        var handler = new Handler(async request =>
        {
            Assert.Equal($"/api/v1/case-imports/{id:D}/context-check", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(new string('a', 64), body.RootElement.GetProperty("fileSha256").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { matches = false }) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://gateway.example/") };
        var client = new WorkspaceImportHttpClient(http);
        var context = new WorkspaceImportContext("workspace", new IntakeConfirmations(true, "SZ", "1", true, true, "/privacy", "1", null, null, null), "org", new string('a', 64));
        Assert.False(await client.MatchesContextAsync(id, context));
    }

    [Fact]
    public async Task ContextCheckRejectsMissingMatchField()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { })
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://gateway.example/") };
        var client = new WorkspaceImportHttpClient(http);
        var context = new WorkspaceImportContext("workspace", new IntakeConfirmations(true, "SZ", "1", true, true, "/privacy", "1", null, null, null), "org", new string('a', 64));
        var error = await Assert.ThrowsAsync<WorkspaceImportException>(() => client.MatchesContextAsync(Guid.NewGuid(), context));
        Assert.Equal("invalid-response", error.Code);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}
