using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftAid.Application.Classification;
using SwiftAid.Contracts.Events;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Classification;
using Xunit.Abstractions;
using NATS.Net;
using NATS.Client.Core;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class ClassificationLiveTests(ITestOutputHelper output)
{
    [ClassificationLiveFact]
    public async Task Real_bart_gateway_commit_jetstream_redelivery_and_poison_quarantine()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var context = await fixture.CreateIsolatedWorkspaceAsync();
        var imported = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), context,
            1, true, new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid()).Replace("\"sn\"", "\"en\"", StringComparison.Ordinal)), default);
        var caseId = imported.Rows.Single().CaseId!.Value;
        // A second identical report and an unsupported-language report exercise real backfill reuse/abstention.
        foreach (var language in new[] { "en", "sn" })
            await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), context, 1, true,
                new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid()).Replace("\"sn\"", $"\"{language}\"", StringComparison.Ordinal)), default);
        await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), context, 1, true,
            new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid(), "[\"No fever reported\"]").Replace("\"sn\"", "\"en\"", StringComparison.Ordinal)), default);
        var source = await fixture.Db.CaseEvents.AsNoTracking().SingleAsync(e => e.CaseId == caseId && e.EventType == "CaseReported");
        var notice = new IngestionNotice(1, source.EventId, "case", caseId, source.AggregateVersion, source.ReportRevision, source.EventType, source.RecordedAtUtc);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.Configure<ClassificationOptions>(options =>
        {
            options.Enabled = true; options.Organizations = ["org-zw-1"];
            options.WorkerTokenSha256 = ClassificationFingerprint.Hash(token);
            options.Profile = options.Profile with { SourceLanguage = "en", TaxonomyVersion = Guid.NewGuid().ToString("N") };
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(fixture.Protection);
        builder.Services.AddScoped(_ => new SwiftAidDbContext(new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql(fixture.Db.Database.GetConnectionString()).UseOpenIddict().Options));
        builder.Services.AddScoped(sp => fixture.ClassificationPayloadStore(sp.GetRequiredService<SwiftAidDbContext>()));
        builder.Services.AddScoped<ClassificationRegistry>();
        builder.Services.AddScoped<ClassificationBackfillStore>();
        await using var app = builder.Build(); app.UseClassificationRequestLimit(); app.MapClassificationWorkerEndpoints(); await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
            (await client.GetAsync(address + "/api/internal/classification/ready")).StatusCode);
        using (var readiness = new HttpRequestMessage(HttpMethod.Get, address + "/api/internal/classification/ready"))
        {
            readiness.Headers.Authorization = new("Bearer", token);
            using var ready = await client.SendAsync(readiness);
            Assert.Equal(System.Net.HttpStatusCode.OK, ready.StatusCode);
            Assert.Contains("ready", await ready.Content.ReadAsStringAsync());
        }
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.PostAsync(address + "/api/internal/classification/claim",
            new StringContent(JsonSerializer.Serialize(new { notice }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"))).StatusCode);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "spec.md"))) root = root.Parent;
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(notice, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var start = new ProcessStartInfo("uv") { WorkingDirectory = root.FullName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "run", "--project", "workers", "--locked", "--extra", "classification", "python", "workers/tests/classification_live.py" }) start.ArgumentList.Add(arg);
        start.Environment["CLASSIFICATION_TEST_GATEWAY"] = address; start.Environment["CLASSIFICATION_TEST_TOKEN"] = token;
        start.Environment["CLASSIFICATION_TEST_NOTICE"] = path; start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["CLASSIFICATION_TEST_WORKSPACE"] = context.WorkspaceId;
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            output.WriteLine(await stdout); output.WriteLine(await stderr); Assert.Equal(0, process.ExitCode);
            var association = await fixture.Db.Set<ClassificationAssociationEntity>().AsNoTracking().SingleAsync(a => a.CaseId == caseId);
            var result = await fixture.Db.Set<ClassificationResultEntity>().AsNoTracking().SingleAsync(r => r.Id == association.ResultId);
            Assert.Equal("Completed", result.State); Assert.Equal(1, result.Attempts); Assert.NotNull(result.PredictionsJson);
            Assert.Equal(1, await fixture.Db.Set<ClassificationEventEntity>().CountAsync(e => e.ResultId == result.Id && e.EventType == "ClassificationCompleted"));
            Assert.True(await fixture.Db.Set<ClassificationQuarantineEntity>().AnyAsync());
            // Exercise the actual metadata-only outbox publication lane after the durable result.
            await using var nats = new NatsClient(new NatsOpts { Url = "nats://127.0.0.1:44229" });
            for (var attempt = 0; attempt < 100 && await fixture.Db.Set<ClassificationEventEntity>().AnyAsync(e => e.ResultId == result.Id && e.PublishedAtUtc == null); attempt++)
            {
                await using var scope = app.Services.CreateAsyncScope();
                var dispatch = new ClassificationOutboxDispatcher(scope.ServiceProvider.GetRequiredService<SwiftAidDbContext>(), nats, TimeProvider.System);
                Assert.True(await dispatch.DispatchOneAsync(default));
            }
            Assert.False(await fixture.Db.Set<ClassificationEventEntity>().AnyAsync(e => e.ResultId == result.Id && e.PublishedAtUtc == null));
        }
        finally { File.Delete(path); }
    }
}

public sealed class ClassificationLiveFactAttribute : FactAttribute
{
    public ClassificationLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SWIFTAID_RUN_CLASSIFICATION_LIVE") != "1" ||
            Environment.GetEnvironmentVariable("SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES") != "1")
            Skip = "Requires explicit opt-in disposable PostgreSQL/NATS and cached pinned BART model.";
    }
}
