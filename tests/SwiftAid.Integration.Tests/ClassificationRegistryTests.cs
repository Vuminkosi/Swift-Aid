using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Classification;
using SwiftAid.Contracts.Events;
using SwiftAid.Domain.Cases;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence.Transactions;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Classification;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class ClassificationRegistryTests
{
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
        var foreign = await SharedWorkspaceImportTests.AddOrganizationAsync(fixture.Db);
        Assert.Null(await query.GetAsync(foreign.Actor, notice.SourceId, default));
        Assert.False((await broker.InvokeAsync(context with { Actor = null }, request, default)).Allowed);
        Assert.False((await broker.InvokeAsync(context with { Actor = foreign.Actor }, request, default)).Allowed);
        Assert.Equal(1, (await fixture.Db.Set<ClassificationResultEntity>().AsNoTracking().SingleAsync(r => r.Id == claim.Lease.ResultId)).Attempts);
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
