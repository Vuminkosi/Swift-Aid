using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Classification;
using SwiftAid.Contracts.Events;
using SwiftAid.Domain.Cases;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence.Transactions;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Classification;
using SwiftAid.Intelligence.Meta;
using System.Diagnostics;
using System.Text.Json;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class ClassificationRegistryTests
{
    [CachedBartPostgresFact]
    public async Task Real_bart_result_is_read_by_framework_without_source_or_lease_disclosure()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var notice = await ImportAsync(fixture);
        var settings = Settings();
        var registry = Registry(fixture, options: settings);
        var lease = (await registry.ClaimAsync(notice, default)).Lease!;
        var container = "ellai-bart-fixture-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in new[] { "run", "--rm", "-i", "--name", container, "--network", "none",
            "--mount", "type=volume,source=swiftaid_classification-model-data,target=/models,readonly",
            "-e", "HF_HOME=/models", "-e", "HF_HUB_OFFLINE=1", "-e", "TRANSFORMERS_OFFLINE=1",
            "--entrypoint", "/app/.venv/bin/python", "swiftaid/classification-worker:ellai-candidate", "-c",
            "import asyncio,json,sys; from swiftaid_workers.classification.bart import BartClassifier; " +
            "json.dump(asyncio.run(BartClassifier().classify(json.load(sys.stdin))),sys.stdout)" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(lease, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            await process.WaitForExitAsync(timeout.Token);
            var json = await output;
            var diagnostics = await errors;
            Assert.True(process.ExitCode == 0, $"BART fixture exited {process.ExitCode}: {diagnostics}");
            var completion = JsonSerializer.Deserialize<ClassificationCompletion>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(completion);
            Assert.NotEmpty(completion.Predictions);
            Assert.Null(completion.AbstentionReason);
            Assert.Equal("Completed", await registry.CompleteAsync(completion, default));
            fixture.Db.ChangeTracker.Clear();
            var unit = new PostgresCaseCommandUnitOfWork(fixture.Db);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(fixture.Db, unit), new PostgresCaseAccessDescriptorStore(fixture.Db, unit));
            var query = new PostgresCaseClassificationQuery(fixture.Db, policy, unit, Options.Create(settings));
            var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), classification: query);
            using var model = new ClassificationToolClient(notice.SourceId);
            var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
                broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
            var work = new EllaiUtteranceWork("real-bart-session", "scope", "Read classification evidence.",
                "public.product-help", null, EllaiMode.Ask, 1, "cycle", CaseImportPostgresTests.Actor, true, "consent/0",
                EllaiProcessingClassification.UserProvidedNonSensitive, EllaiProcessingClassification.UserProvidedNonSensitive,
                new HashSet<string> { EllaiToolNames.ClassificationEvidence }, EllaiConsentService.PublicProductHelpPurpose, "turn");
            var turn = await EllaiUtteranceRunner.RunOneAsync(work,
                new EllaiUtteranceServices(executor, null, null, new EllaiConsentService(), InvokeToolAsync: broker.InvokeAsync), 1024, default);
            Assert.True(turn.Completed, turn.FailureCode);
            Assert.True(model.SawToolResult);
            Assert.Contains(lease.ResultId.ToString("D"), model.ToolResultText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(lease.LeaseId.ToString("D"), model.ToolResultText);
            Assert.DoesNotContain(lease.Text, model.ToolResultText);
        }
        finally
        {
            if (!process.HasExited)
            {
                using var stop = Process.Start(new ProcessStartInfo("docker")
                {
                    ArgumentList = { "stop", container }, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                });
                if (stop is not null) await stop.WaitForExitAsync();
                await process.WaitForExitAsync();
            }
        }
    }

    private sealed class CachedBartPostgresFactAttribute : FactAttribute
    {
        public CachedBartPostgresFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SWIFTAID_RUN_CLASSIFICATION_LIVE") != "1" ||
                Environment.GetEnvironmentVariable("SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES") != "1")
                Skip = "Requires opt-in disposable PostgreSQL, the local classification image and cached BART volume.";
        }
    }

    [IntakePostgresFact]
    public async Task Newer_projection_revision_emits_one_durable_supersession_without_replacing_predictions()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var context = await fixture.CreateIsolatedWorkspaceAsync();
        var notice = await ImportAsync(fixture, context);
        var registry = Registry(fixture);
        var original = (await registry.ClaimAsync(notice, default)).Lease!;
        await registry.CompleteAsync(Completion(original), default);
        // Consumer-boundary fixture: simulate an already committed newer projection.
        // The existing legacy narrative-correction command intentionally rejects typed v2 imports.
        await fixture.Db.ManagedCases.Where(c => c.CaseId == notice.SourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ReportRevision, 2));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("Superseded", (await registry.ClaimAsync(notice, default)).State);
        Assert.Equal("Superseded", (await registry.ClaimAsync(notice, default)).State);
        var events = await fixture.Db.Set<ClassificationEventEntity>().Where(e => e.CaseId == notice.SourceId && e.EventType == "ClassificationSuperseded").ToListAsync();
        Assert.Equal(original.ResultId, Assert.Single(events).ResultId);
        Assert.Equal(1, events[0].ReportRevision);
        Assert.Equal("Completed", (await fixture.Db.Set<ClassificationResultEntity>().FindAsync(original.ResultId))!.State);
    }

    [IntakePostgresFact]
    public async Task Backfill_snapshots_revisions_resumes_after_restart_and_reconciles_without_reinference()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var context = await fixture.CreateIsolatedWorkspaceAsync();
        var notice = await ImportAsync(fixture, context);
        var options = Settings();
        var registry = Registry(fixture, options: options);
        var lease = (await registry.ClaimAsync(notice, default)).Lease!;
        await registry.CompleteAsync(Completion(lease), default);
        var service = new ClassificationBackfillStore(fixture.Db, Options.Create(options), TimeProvider.System);
        var request = new StartClassificationBackfill(Guid.NewGuid(), context.WorkspaceId, "org-zw-1");
        var started = await service.StartAsync(request, default);
        await service.BindResultAsync(request.JobId, notice.MessageId, lease.ConfigurationHash, default);
        Assert.True(started.Eligible >= 1);
        Assert.Equal(started.Eligible, (await service.StartAsync(request, default)).Eligible);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.StartAsync(request with { OrganizationId = "other-org" }, default));
        await service.ControlAsync(request.JobId, true, default);
        Assert.Null((await service.NextAsync(request.JobId, default)).Notice);
        await ImportAsync(fixture, context); // A later accepted revision must not enter this snapshot.
        await using var reopened = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var resumed = new ClassificationBackfillStore(reopened, Options.Create(options), TimeProvider.System);
        Assert.Equal("Paused", (await resumed.StatusAsync(request.JobId, default)).State);
        await resumed.ControlAsync(request.JobId, false, default);
        var work = await resumed.NextAsync(request.JobId, default);
        Assert.Equal(started.Eligible, work.Status.Eligible);
        Assert.True(work.Status.Reused >= 1);
        Assert.Equal(work.Status.Eligible, work.Status.Pending + work.Status.NewlyClassified + work.Status.Reused + work.Status.Abstained + work.Status.Failed);
        Assert.Equal(1, (await reopened.Set<ClassificationResultEntity>().FindAsync(lease.ResultId))!.Attempts);
        options.Profile = options.Profile with { TaxonomyVersion = "changed-after-snapshot" };
        Assert.Equal("ConfigurationChanged", (await resumed.NextAsync(request.JobId, default)).Status.State);
    }

    private static ClassificationOptions Settings() => new()
    {
        Enabled = true, WorkerTokenSha256 = new string('a', 64), Organizations = ["org-zw-1"],
        Profile = new ClassificationOptions().Profile with { SourceLanguage = "en", TaxonomyVersion = Guid.NewGuid().ToString("N") }
    };

    private static ClassificationRegistry Registry(CaseImportPostgresTests.Fixture fixture, SwiftAidDbContext? db = null,
        ClassificationOptions? options = null, TimeProvider? clock = null) => new(db ?? fixture.Db,
            fixture.ClassificationPayloadStore(db ?? fixture.Db), Options.Create(options ?? Settings()), clock ?? TimeProvider.System, fixture.Protection);

    private static async Task<IngestionNotice> ImportAsync(CaseImportPostgresTests.Fixture fixture,
        SwiftAid.Application.Intake.Imports.CaseImportContext? context = null)
    {
        var result = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), context ?? CaseImportPostgresTests.Context,
            1, true, new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid()).Replace("\"sn\"", "\"en\"", StringComparison.Ordinal)), default);
        var id = Assert.Single(result.Rows).CaseId!.Value;
        var source = await fixture.Db.CaseEvents.AsNoTracking().SingleAsync(e => e.CaseId == id && e.EventType == "CaseReported");
        fixture.Db.ChangeTracker.Clear();
        return new(1, source.EventId, "case", id, source.AggregateVersion, source.ReportRevision, source.EventType, source.RecordedAtUtc);
    }

    private static ClassificationCompletion Completion(ClassificationLease lease) => new(lease.ResultId, lease.LeaseId,
        lease.ConfigurationHash, lease.Fingerprint, lease.Profile.Labels.Select(l => new ClassificationPrediction(l.Id, 0.4)).ToArray(), null, 20, 4);

    [IntakePostgresFact]
    public async Task Result_commit_precedes_redelivery_ack_and_predictions_cannot_be_replaced()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var notice = await ImportAsync(fixture);
        var registry = Registry(fixture);
        var claim = await registry.ClaimAsync(notice, default);
        Assert.Equal("Leased", claim.State);
        var quality = await fixture.Db.Set<ReportQualityEntity>().SingleAsync(q => q.SourceEventId == notice.MessageId);
        Assert.Contains("geography-not-exact", quality.AssessmentJson);
        Assert.NotEmpty(claim.Lease!.Text);
        Assert.Equal("Busy", (await registry.ClaimAsync(notice, default)).State);
        var complete = Completion(claim.Lease);
        Assert.Equal("Completed", await registry.CompleteAsync(complete, default));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("Completed", (await registry.ClaimAsync(notice, default)).State);
        Assert.Equal("Completed", await registry.CompleteAsync(complete, default));
        Assert.Equal("Conflict", await registry.CompleteAsync(complete with { InferenceMilliseconds = 123 }, default));
        Assert.Equal(1, await fixture.Db.Set<ClassificationEventEntity>().CountAsync(e => e.ResultId == claim.Lease.ResultId && e.EventType == "ClassificationCompleted"));
        Assert.Equal(ClinicalClassification.Unclassified, (await fixture.Db.ManagedCases.FindAsync(notice.SourceId))!.Classification);
        Assert.Equal(1, await fixture.Db.Set<ReportQualityEntity>().CountAsync(q => q.SourceEventId == notice.MessageId));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Db.Set<ReportQualityEntity>()
            .Where(q => q.SourceEventId == notice.MessageId).ExecuteUpdateAsync(s => s.SetProperty(q => q.AssessmentJson, "{}")));
    }

    [IntakePostgresFact]
    public async Task Same_content_reuses_result_but_configuration_upgrade_requires_new_inference()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var first = await ImportAsync(fixture);
        var options = Settings(); options.Profile = options.Profile with { TaxonomyVersion = Guid.NewGuid().ToString("N") };
        var registry = Registry(fixture, options: options);
        var claim = await registry.ClaimAsync(first, default);
        Assert.Equal("Completed", await registry.CompleteAsync(Completion(claim.Lease!), default));
        var second = await ImportAsync(fixture);
        Assert.Equal("Completed", (await registry.ClaimAsync(second, default)).State);
        var association = await fixture.Db.Set<ClassificationAssociationEntity>().SingleAsync(a => a.CaseId == second.SourceId);
        Assert.Equal(claim.Lease!.ResultId, association.ResultId);
        Assert.True(association.Reused);
        options.Profile = options.Profile with { ModelRevision = new string('b', 40) };
        var upgraded = await registry.ClaimAsync(second, default);
        Assert.Equal("Leased", upgraded.State);
        Assert.NotEqual(claim.Lease.ResultId, upgraded.Lease!.ResultId);
        Assert.Equal(2, await fixture.Db.Set<ClassificationAssociationEntity>().CountAsync(a => a.CaseId == second.SourceId));
    }

    [IntakePostgresFact]
    public async Task Expired_and_replaced_leases_are_fenced_and_exhaustion_is_durable()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var notice = await ImportAsync(fixture);
        var clock = new MutableClock();
        var options = Settings(); options.MaximumAttempts = 2;
        options.Profile = options.Profile with { TaxonomyVersion = Guid.NewGuid().ToString("N") };
        var registry = Registry(fixture, options: options, clock: clock);
        var first = (await registry.ClaimAsync(notice, default)).Lease!;
        clock.Now = clock.Now.AddSeconds(181);
        Assert.Equal("StaleLease", await registry.CompleteAsync(Completion(first), default));
        var second = (await registry.ClaimAsync(notice, default)).Lease!;
        Assert.NotEqual(first.LeaseId, second.LeaseId);
        Assert.Equal("StaleLease", await registry.CompleteAsync(Completion(first), default));
        clock.Now = clock.Now.AddSeconds(181);
        Assert.Equal("Failed", (await registry.ClaimAsync(notice, default)).State);
        Assert.Equal("lease-expired", (await fixture.Db.Set<ClassificationResultEntity>().FindAsync(first.ResultId))!.FailureCode);
    }

    [IntakePostgresFact]
    public async Task Worker_scope_and_committed_source_identity_are_required()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var notice = await ImportAsync(fixture);
        var options = Settings(); options.Organizations = ["another-org"];
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Registry(fixture, options: options).ClaimAsync(notice, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Registry(fixture).ClaimAsync(notice with { SourceVersion = 999 }, default));
        Assert.False(await fixture.Db.Set<ClassificationInboxEntity>().AnyAsync(i => i.MessageId == notice.MessageId));
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [IntakePostgresFact]
    public async Task Authorized_query_and_ellai_read_persisted_results_without_inference_or_lease_disclosure()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var notice = await ImportAsync(fixture);
        var settings = Settings();
        var registry = Registry(fixture, options: settings);
        var claim = await registry.ClaimAsync(notice, default);
        await registry.CompleteAsync(Completion(claim.Lease!), default);
        fixture.Db.ChangeTracker.Clear();
        var unit = new PostgresCaseCommandUnitOfWork(fixture.Db);
        var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(fixture.Db, unit), new PostgresCaseAccessDescriptorStore(fixture.Db, unit));
        var query = new PostgresCaseClassificationQuery(fixture.Db, policy, unit, Options.Create(settings));
        var items = await query.GetAsync(CaseImportPostgresTests.Actor, notice.SourceId, default);
        var evidence = Assert.Single(items!);
        Assert.NotNull(evidence.Quality);
        Assert.Contains(evidence.Quality.Findings, f => f.Code == "geography-not-exact");
        Assert.Equal(claim.Lease!.ResultId, evidence.ResultId); Assert.Equal("en", evidence.SourceLanguage);
        Assert.True(evidence.CurrentConfiguration); Assert.Equal(settings.Profile.Labels.Count, evidence.Labels.Count);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), classification: query);
        var context = new EllaiToolContext("classification-session", "classification-scope", "public.product-help", null, 4, true,
            Actor: CaseImportPostgresTests.Actor, DataClassification: EllaiProcessingClassification.UserProvidedNonSensitive,
            ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose);
        var request = new EllaiToolRequest(EllaiToolNames.ClassificationEvidence, new Dictionary<string, string> { ["case_id"] = notice.SourceId.ToString("D") });
        var response = await broker.InvokeAsync(context, request, default);
        Assert.True(response.Allowed); Assert.DoesNotContain(claim.Lease.LeaseId.ToString("D"), response.ResultSummary);
        Assert.DoesNotContain("Original supplied concern", response.ResultSummary);
        using var model = new ClassificationToolClient(notice.SourceId);
        var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
            broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var observations = new List<EllaiToolResult>();
        var work = new EllaiUtteranceWork(context.SessionId, context.ScopeEpoch, "Read the classification evidence.",
            context.FeatureId, null, EllaiMode.Ask, 1, "cycle", CaseImportPostgresTests.Actor, true, "consent/0",
            EllaiProcessingClassification.UserProvidedNonSensitive, EllaiProcessingClassification.UserProvidedNonSensitive,
            new HashSet<string> { EllaiToolNames.ClassificationEvidence }, EllaiConsentService.PublicProductHelpPurpose, "turn");
        var turn = await EllaiUtteranceRunner.RunOneAsync(work, new EllaiUtteranceServices(executor, null, null,
            new EllaiConsentService(), InvokeToolAsync: async (toolContext, toolRequest, ct) =>
            {
                var observation = await broker.InvokeAsync(toolContext, toolRequest, ct);
                observations.Add(observation);
                return observation;
            }), 1024, default);
        Assert.True(turn.Completed, turn.FailureCode);
        Assert.NotEmpty(observations);
        Assert.All(observations, observation => Assert.True(observation.Allowed));
        Assert.True(model.SawToolResult);
        Assert.Contains(claim.Lease.ResultId.ToString("D"), model.ToolResultText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(claim.Lease.LeaseId.ToString("D"), model.ToolResultText);
        Assert.DoesNotContain("Original supplied concern", model.ToolResultText);
        Assert.True(observations.Count >= 2); // The runner re-reads before publishing.
        var foreign = await SharedWorkspaceImportTests.AddOrganizationAsync(fixture.Db);
        Assert.Null(await query.GetAsync(foreign.Actor, notice.SourceId, default));
        Assert.False((await broker.InvokeAsync(context with { Actor = null }, request, default)).Allowed);
        Assert.False((await broker.InvokeAsync(context with { Actor = foreign.Actor }, request, default)).Allowed);
        foreach (var change in new[] { "revoked", "changed" })
        {
            using var changedModel = new ClassificationToolClient(notice.SourceId);
            var changedExecutor = new EllaiFrameworkExecutor(changedModel, new EllaiPromptBuilder("Synthetic policy", "fixture"),
                broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
            var invocations = 0;
            var blocked = await EllaiUtteranceRunner.RunOneAsync(work with { CycleId = change },
                new EllaiUtteranceServices(changedExecutor, null, null, new EllaiConsentService(),
                    InvokeToolAsync: async (toolContext, toolRequest, ct) =>
                    {
                        if (++invocations == 2 && change == "changed")
                            await fixture.Db.Set<ClassificationResultEntity>().Where(result => result.Id == claim.Lease.ResultId)
                                .ExecuteUpdateAsync(setters => setters.SetProperty(result => result.State, "Failed"), ct);
                        return await broker.InvokeAsync(invocations >= 2 && change == "revoked"
                            ? toolContext with { Actor = foreign.Actor } : toolContext, toolRequest, ct);
                    }), 1024, default);
            Assert.False(blocked.Completed);
            Assert.Null(blocked.DisplayText);
            Assert.True(invocations >= 2);
        }
        Assert.Equal(1, (await fixture.Db.Set<ClassificationResultEntity>().AsNoTracking().SingleAsync(r => r.Id == claim.Lease.ResultId)).Attempts);
    }

    private sealed class ClassificationToolClient(Guid caseId) : IChatClient
    {
        private int _calls;
        public bool SawToolResult { get; private set; }
        public string ToolResultText { get; private set; } = string.Empty;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("classification", EllaiToolNames.ClassificationEvidence,
                        new Dictionary<string, object?> { ["case_id"] = caseId.ToString("D") })])));
            var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            SawToolResult = results.Any(result => result.CallId == "classification");
            ToolResultText = System.Text.Json.JsonSerializer.Serialize(results);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.","spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [IntakePostgresFact]
    public async Task Source_language_overrides_the_worker_fallback_and_unsupported_language_abstains()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var imported = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), CaseImportPostgresTests.Context,
            1, true, new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
        var id = imported.Rows.Single().CaseId!.Value;
        var source = await fixture.Db.CaseEvents.AsNoTracking().SingleAsync(e => e.CaseId == id && e.EventType == "CaseReported");
        fixture.Db.ChangeTracker.Clear();
        var registry = Registry(fixture);
        var claim = await registry.ClaimAsync(new(1, source.EventId, "case", id, source.AggregateVersion, source.ReportRevision, source.EventType, source.RecordedAtUtc), default);
        Assert.Equal("sn", claim.Lease!.Profile.SourceLanguage);
        await Assert.ThrowsAsync<ArgumentException>(() => registry.CompleteAsync(Completion(claim.Lease), default));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("Completed", await registry.CompleteAsync(Completion(claim.Lease) with
            { Predictions = [], AbstentionReason = "unsupported-language", InputTokens = 0 }, default));
    }

    [IntakePostgresFact]
    public async Task Concurrent_claims_grant_one_lease_and_renewal_is_bounded()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var notice = await ImportAsync(fixture);
        var options = Settings();
        await using var other = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var clock = new MutableClock();
        var registry = Registry(fixture, options: options, clock: clock);
        var claims = await Task.WhenAll(registry.ClaimAsync(notice, default), Registry(fixture, other, options, clock).ClaimAsync(notice, default));
        Assert.Single(claims, c => c.State == "Busy");
        var lease = Assert.Single(claims, c => c.State == "Leased").Lease!;
        fixture.Db.ChangeTracker.Clear();
        clock.Now = clock.Now.AddSeconds(100);
        Assert.Equal("Leased", await registry.RenewAsync(new(lease.ResultId, lease.LeaseId), default));
        clock.Now = clock.Now.AddMinutes(10);
        Assert.Equal("StaleLease", await registry.RenewAsync(new(lease.ResultId, lease.LeaseId), default));
    }

    [IntakePostgresFact]
    public async Task Completed_artifacts_and_source_associations_are_immutable_in_postgres()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var notice = await ImportAsync(fixture);
        var registry = Registry(fixture);
        var lease = (await registry.ClaimAsync(notice, default)).Lease!;
        await registry.CompleteAsync(Completion(lease), default);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Db.Set<ClassificationResultEntity>().Where(r => r.Id == lease.ResultId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, "Pending")));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Db.Set<ClassificationAssociationEntity>().Where(a => a.CaseId == notice.SourceId).ExecuteDeleteAsync());
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Db.Set<ClassificationConfigurationEntity>().Where(c => c.Id == lease.ConfigurationHash)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ProfileJson, "{}")));
    }
}
