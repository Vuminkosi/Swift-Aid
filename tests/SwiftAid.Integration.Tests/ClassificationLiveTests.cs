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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Classification;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Events;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Classification;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence.Transactions;
using SwiftAid.Intelligence.Meta;
using Xunit.Abstractions;
using NATS.Net;
using NATS.Client.Core;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class ClassificationLiveTests(ITestOutputHelper output)
{
    [ClassificationLiveFact]
    public Task Real_bart_gateway_commit_jetstream_redelivery_and_poison_quarantine() => RunAsync(liveMuse: false);

    [ClassificationMuseLiveFact]
    public Task Live_muse_reads_jetstream_delivered_real_bart_evidence() => RunAsync(liveMuse: true);

    private async Task RunAsync(bool liveMuse)
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
        var dockerRunner = Environment.GetEnvironmentVariable("SWIFTAID_CLASSIFICATION_DOCKER_FIXTURE") == "1";
        var natsUrl = Environment.GetEnvironmentVariable("SWIFTAID_TEST_INGESTION_NATS_URL") ?? "nats://127.0.0.1:44229";
        Assert.True(Uri.TryCreate(natsUrl, UriKind.Absolute, out var natsUri) && natsUri.IsLoopback && natsUri.Scheme == "nats");
        var container = "ellai-classification-live-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(dockerRunner ? "docker" : "uv") { WorkingDirectory = root.FullName,
            RedirectStandardInput = dockerRunner, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var args = dockerRunner ? new[] { "run", "--rm", "-i", "--name", container, "--network", "bridge",
            "--mount", "type=volume,source=swiftaid_classification-model-data,target=/models,readonly",
            "-e", "HF_HOME=/models", "-e", "HF_HUB_OFFLINE=1", "-e", "TRANSFORMERS_OFFLINE=1",
            "-e", "CLASSIFICATION_TEST_GATEWAY", "-e", "CLASSIFICATION_TEST_TOKEN", "-e", "CLASSIFICATION_TEST_NOTICE_JSON",
            "-e", "CLASSIFICATION_TEST_WORKSPACE", "-e", "CLASSIFICATION_TEST_NATS", "-e", "CLASSIFICATION_TEST_HOST_HEADER",
            "--entrypoint", "/app/.venv/bin/python", "swiftaid/classification-worker:ellai-candidate", "-c", "import sys; exec(sys.stdin.read())" }
            : new[] { "run", "--project", "workers", "--locked", "--extra", "classification", "python", "workers/tests/classification_live.py" };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["CLASSIFICATION_TEST_GATEWAY"] = dockerRunner ? address.Replace("127.0.0.1", "host.docker.internal", StringComparison.Ordinal) : address;
        if (dockerRunner) start.Environment["CLASSIFICATION_TEST_HOST_HEADER"] = new Uri(address).Authority;
        start.Environment["CLASSIFICATION_TEST_TOKEN"] = token;
        start.Environment["CLASSIFICATION_TEST_NOTICE"] = path; start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["CLASSIFICATION_TEST_NOTICE_JSON"] = JsonSerializer.Serialize(notice, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        start.Environment["CLASSIFICATION_TEST_NATS"] = dockerRunner ? $"nats://host.docker.internal:{natsUri!.Port}" : natsUrl;
        start.Environment["CLASSIFICATION_TEST_WORKSPACE"] = context.WorkspaceId;
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (dockerRunner)
            {
                await process.StandardInput.WriteAsync(await File.ReadAllTextAsync(Path.Combine(root.FullName, "workers", "tests", "classification_live.py")));
                process.StandardInput.Close();
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally
            {
                if (!process.HasExited && dockerRunner)
                {
                    using var stop = Process.Start(new ProcessStartInfo("docker") { ArgumentList = { "stop", container },
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                    if (stop is not null) await stop.WaitForExitAsync();
                    await process.WaitForExitAsync();
                }
                else if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            output.WriteLine(await stdout); output.WriteLine(await stderr); Assert.Equal(0, process.ExitCode);
            var association = await fixture.Db.Set<ClassificationAssociationEntity>().AsNoTracking().SingleAsync(a => a.CaseId == caseId);
            var result = await fixture.Db.Set<ClassificationResultEntity>().AsNoTracking().SingleAsync(r => r.Id == association.ResultId);
            Assert.Equal("Completed", result.State); Assert.Equal(1, result.Attempts); Assert.NotNull(result.PredictionsJson);
            Assert.Equal(1, await fixture.Db.Set<ClassificationEventEntity>().CountAsync(e => e.ResultId == result.Id && e.EventType == "ClassificationCompleted"));
            Assert.True(await fixture.Db.Set<ClassificationQuarantineEntity>().AnyAsync());
            // Exercise the actual metadata-only outbox publication lane after the durable result.
            await using var nats = new NatsClient(new NatsOpts { Url = natsUrl });
            for (var attempt = 0; attempt < 100 && await fixture.Db.Set<ClassificationEventEntity>().AnyAsync(e => e.ResultId == result.Id && e.PublishedAtUtc == null); attempt++)
            {
                await using var scope = app.Services.CreateAsyncScope();
                var dispatch = new ClassificationOutboxDispatcher(scope.ServiceProvider.GetRequiredService<SwiftAidDbContext>(), nats, TimeProvider.System);
                Assert.True(await dispatch.DispatchOneAsync(default));
            }
            Assert.False(await fixture.Db.Set<ClassificationEventEntity>().AnyAsync(e => e.ResultId == result.Id && e.PublishedAtUtc == null));
            var unit = new PostgresCaseCommandUnitOfWork(fixture.Db);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(fixture.Db, unit),
                new PostgresCaseAccessDescriptorStore(fixture.Db, unit));
            var query = new PostgresCaseClassificationQuery(fixture.Db, policy, unit,
                app.Services.GetRequiredService<IOptions<ClassificationOptions>>());
            var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), classification: query);
            using var scripted = liveMuse ? null : new FrameworkClassificationClient(caseId);
            using var live = liveMuse ? new LiveClassificationCaptureClient(EllaiMuseChatClientFactory.Create(EllaiOptions.Bind(Environment.GetEnvironmentVariable))) : null;
            var executor = new EllaiFrameworkExecutor((IChatClient?)live ?? scripted!, new EllaiPromptBuilder("Synthetic policy", "fixture"),
                broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
            var utterance = liveMuse
                ? $"Use ellai_classification_evidence with case_id {caseId:D} to read the persisted semantic classification. Explain briefly that it is not a diagnosis."
                : "Read the classification.";
            var work = new EllaiUtteranceWork("live-classification-session", "scope", utterance,
                "public.product-help", null, EllaiMode.Ask, 1, "cycle", CaseImportPostgresTests.Actor, true, "consent/0",
                EllaiProcessingClassification.UserProvidedNonSensitive, EllaiProcessingClassification.UserProvidedNonSensitive,
                new HashSet<string> { EllaiToolNames.ClassificationEvidence }, EllaiConsentService.PublicProductHelpPurpose, "turn");
            var turn = await EllaiUtteranceRunner.RunOneAsync(work,
                new EllaiUtteranceServices(executor, null, null, new EllaiConsentService(), InvokeToolAsync: broker.InvokeAsync), 1024, default);
            Assert.True(turn.Completed, turn.FailureCode);
            var toolResultText = live?.ToolResultText ?? scripted!.ToolResultText;
            Assert.Contains(result.Id.ToString("D"), toolResultText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Original supplied concern", toolResultText);
        }
        finally { File.Delete(path); }
    }

    private sealed class FrameworkClassificationClient(Guid caseId) : IChatClient
    {
        private int _calls;
        public string ToolResultText { get; private set; } = string.Empty;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (++_calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("classification", EllaiToolNames.ClassificationEvidence,
                        new Dictionary<string, object?> { ["case_id"] = caseId.ToString("D") })])));
            ToolResultText = JsonSerializer.Serialize(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.","spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class LiveClassificationCaptureClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public string ToolResultText { get; private set; } = string.Empty;
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var copied = messages.ToArray();
            var results = copied.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            if (results.Length != 0) ToolResultText = JsonSerializer.Serialize(results);
            return base.GetResponseAsync(copied, options, cancellationToken);
        }
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

public sealed class ClassificationMuseLiveFactAttribute : FactAttribute
{
    public ClassificationMuseLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ELLAI_LIVE") != "1" ||
            Environment.GetEnvironmentVariable("SWIFTAID_RUN_CLASSIFICATION_LIVE") != "1" ||
            Environment.GetEnvironmentVariable("SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES") != "1")
            Skip = "Requires live Muse and disposable PostgreSQL/NATS with the cached pinned BART model.";
    }
}
