using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Classification;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Application.Surveillance;
using SwiftAid.Contracts.Events;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Surveillance;
using SwiftAid.Infrastructure.Persistence.Transactions;
using SwiftAid.Infrastructure.Surveillance;
using Xunit.Abstractions;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class SurveillanceLoopTests(ITestOutputHelper output)
{
    [IntakePostgresFact]
    public async Task Imported_classified_reports_create_durable_authorized_reviewable_candidates_and_replay_safely()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var context = await fixture.CreateIsolatedWorkspaceAsync();
        var settings = Settings(context.WorkspaceId);
        var classifier = Classifier();
        var ids = new List<Guid>();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < 3; i++) ids.Add(await ImportAsync(fixture, context, classifier, "source-" + Guid.NewGuid().ToString("N")));
        var imported = Stopwatch.GetElapsedTime(started);
        await DrainAsync(fixture, settings, classifier);
        var processed = Stopwatch.GetElapsedTime(started) - imported;
        var signals = await fixture.Db.Set<SurveillanceSignalEntity>().AsNoTracking().Where(s => s.WorkspaceId == context.WorkspaceId && s.Candidate).ToArrayAsync();
        Assert.NotEmpty(signals);
        Assert.Contains(signals, s => s.Area.StartsWith("locality:", StringComparison.Ordinal));
        var signal = signals.First(s => s.Area.StartsWith("region:", StringComparison.Ordinal));
        var originalAssessment = signal.AssessmentId;
        var count = await fixture.Db.Set<DetectionAssessmentEntity>().CountAsync(a => a.SignalId == signal.Id);
        await using var restarted = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        Assert.False(await Processor(fixture, restarted, settings, classifier).ProcessOneAsync(default));
        Assert.Equal(count, await restarted.Set<DetectionAssessmentEntity>().CountAsync(a => a.SignalId == signal.Id));
        using var services = AuthorityServices(fixture.Db);
        var review = new PostgresSignalReviewService(restarted, services.GetRequiredService<IServiceScopeFactory>(), Options.Create(settings), TimeProvider.System);
        Assert.Null(await review.GetAsync(CaseRegistrationBackendTests.CreateOfficer("outsider"), signal.Id, default));
        var dossier = await review.GetAsync(CaseImportPostgresTests.Actor, signal.Id, default);
        Assert.NotNull(dossier);
        Assert.Equal(3, dossier.Assessment.Observed);
        Assert.Equal(3, dossier.Assessment.IndependentSources);
        Assert.True(dossier.Assessment.WindowEndUtc.Year == 2026 && dossier.Assessment.WindowEndUtc.Month == 6);
        Assert.Null(dossier.Assessment.Expected);
        var command = new SignalDecisionCommand(Guid.NewGuid(), signal.Id, signal.Version, signal.AssessmentId, "UnderVerification", "Synthetic source records checked.");
        Assert.Equal("Completed", (await review.DecideAsync(command, CaseImportPostgresTests.Actor, default)).Outcome);
        restarted.ChangeTracker.Clear();
        Assert.Equal("Completed", (await review.DecideAsync(command, CaseImportPostgresTests.Actor, default)).Outcome);
        Assert.Equal("Conflict", (await review.DecideAsync(command with { CommandId = Guid.NewGuid() }, CaseImportPostgresTests.Actor, default)).Outcome);
        restarted.ChangeTracker.Clear();
        Assert.Equal("Completed", (await review.DecideAsync(command with { CommandId = Guid.NewGuid(), ExpectedVersion = signal.Version + 1, State = "Verified" }, CaseImportPostgresTests.Actor, default)).Outcome);
        Assert.Equal(2, await restarted.Set<SurveillanceReviewEntity>().CountAsync(r => r.SignalId == signal.Id));
        // A fourth independent source reopens review on new evidence; prior officer decisions remain immutable.
        await ImportAsync(fixture, context, classifier, "source-" + Guid.NewGuid().ToString("N"));
        await DrainAsync(fixture, settings, classifier);
        restarted.ChangeTracker.Clear();
        var updated = await restarted.Set<SurveillanceSignalEntity>().AsNoTracking().SingleAsync(s => s.Id == signal.Id);
        Assert.Equal("Candidate", updated.ReviewState);
        Assert.NotEqual(originalAssessment, updated.AssessmentId);
        Assert.Equal(2, await restarted.Set<SurveillanceReviewEntity>().CountAsync(r => r.SignalId == signal.Id));
        Assert.Equal("Conflict", (await review.DecideAsync(command with { CommandId = Guid.NewGuid() }, CaseImportPostgresTests.Actor, default)).Outcome);
        output.WriteLine($"Synthetic 3-report import/classification fixture: {imported.TotalMilliseconds:F1} ms; detection: {processed.TotalMilliseconds:F1} ms; candidate area/windows: {signals.Length}. No live model inference.");
    }

    [IntakePostgresFact]
    public async Task Assessments_and_reviews_cannot_be_rewritten_in_the_database()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var context = await fixture.CreateIsolatedWorkspaceAsync();
        var classifier = Classifier();
        for (var i = 0; i < 3; i++) await ImportAsync(fixture, context, classifier, "source-" + Guid.NewGuid().ToString("N"));
        await DrainAsync(fixture, Settings(context.WorkspaceId), classifier);
        var signal = await fixture.Db.Set<SurveillanceSignalEntity>().FirstAsync(s => s.WorkspaceId == context.WorkspaceId);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Db.Set<DetectionAssessmentEntity>().Where(a => a.Id == signal.AssessmentId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AssessmentJson, "{}")));
    }

    [IntakePostgresFact]
    public async Task Disabled_gate_creates_no_work_and_classifier_absence_does_not_block_admission()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var context = await fixture.CreateIsolatedWorkspaceAsync();
        var classifier = Classifier();
        var id = await ImportAsync(fixture, context, classifier, "source-" + Guid.NewGuid().ToString("N"), classify: false);
        Assert.False(await Processor(fixture, fixture.Db, new(), classifier).ProcessOneAsync(default));
        Assert.False(await fixture.Db.Set<SurveillanceWorkEntity>().AnyAsync(w => w.CaseId == id));
        await DrainAsync(fixture, Settings(context.WorkspaceId), classifier);
        Assert.True(await fixture.Db.Set<SurveillanceInputEntity>().AnyAsync(i => i.CaseId == id));
        Assert.False(await fixture.Db.Set<SurveillanceSignalEntity>().AnyAsync(s => s.WorkspaceId == context.WorkspaceId));
    }

    private static SurveillanceOptions Settings(string workspace) => new() { Enabled = true, Mode = "SyntheticEvaluation", Workspaces = [workspace], Organizations = ["org-zw-1"] };
    private static ClassificationOptions Classifier() => new() { Enabled = true, Organizations = ["org-zw-1"], WorkerTokenSha256 = new string('a', 64),
        Profile = new ClassificationOptions().Profile with { SourceLanguage = "en", TaxonomyVersion = Guid.NewGuid().ToString("N") } };
    private static SurveillanceProcessor Processor(CaseImportPostgresTests.Fixture fixture, SwiftAidDbContext db, SurveillanceOptions settings, ClassificationOptions classifier) =>
        new(db, fixture.ClassificationPayloadStore(db), Options.Create(settings), Options.Create(classifier), TimeProvider.System);

    private static async Task DrainAsync(CaseImportPostgresTests.Fixture fixture, SurveillanceOptions settings, ClassificationOptions classifier)
    {
        for (var i = 0; i < 20; i++)
        {
            fixture.Db.ChangeTracker.Clear();
            if (!await Processor(fixture, fixture.Db, settings, classifier).ProcessOneAsync(default)) break;
        }
        var failures = await (from work in fixture.Db.Set<SurveillanceWorkEntity>() join header in fixture.Db.ManagedCases on work.CaseId equals header.CaseId
                              where header.WorkspaceId == settings.Workspaces[0] && work.State != "Completed" select work).ToArrayAsync();
        Assert.True(failures.Length == 0, string.Join(',', failures.Select(f => f.State + ":" + f.FailureCode)));
    }

    private static async Task<Guid> ImportAsync(CaseImportPostgresTests.Fixture fixture, CaseImportContext context,
        ClassificationOptions classifier, string actorId, bool classify = true)
    {
        var membership = await IntakeDecisionStoreTests.ResetOfficerScopeAsync(fixture.Db, actorId, 7);
        var grant = await fixture.Db.AccessGrantGroups.AsNoTracking().FirstAsync(g => g.WorkspaceId == context.WorkspaceId && g.ActorId == CaseImportPostgresTests.Actor.ActorId);
        var reviewerGrantId = grant.GrantGroupId;
        grant.GrantGroupId = Guid.NewGuid().ToString("N"); grant.ActorId = actorId; grant.MembershipId = membership;
        fixture.Db.Add(grant);
        fixture.Db.Add(new AccessGrantOperationEntity { GrantGroupId = grant.GrantGroupId, Operation = CaseOperation.ReadCase });
        foreach (var operation in new[] { CaseOperation.ReadLinkedSignalEvidence, CaseOperation.ReviewProtectedAction })
            if (!await fixture.Db.AccessGrantOperations.AnyAsync(o => o.GrantGroupId == reviewerGrantId && o.Operation == operation))
                fixture.Db.Add(new AccessGrantOperationEntity { GrantGroupId = reviewerGrantId, Operation = operation });
        await fixture.Db.SaveChangesAsync(); fixture.Db.ChangeTracker.Clear();
        var csv = CaseImportPostgresTests.Csv(Guid.NewGuid()).Replace("\"sn\"", "\"en\"", StringComparison.Ordinal)
            .Replace("Manicaland", "SYNTHETIC REGION", StringComparison.Ordinal).Replace("Rusape", "SYNTHETIC LOCALITY", StringComparison.Ordinal)
            .Replace("ZW:place:rusape", "ZW:test:locality", StringComparison.Ordinal);
        var imported = await fixture.Service.ProcessAsync(CaseRegistrationBackendTests.CreateOfficer(actorId), Guid.NewGuid(), context, 1, true, new StringReader(csv), default);
        var row = Assert.Single(imported.Rows);
        Assert.True(row.CaseId.HasValue, row.ErrorCode);
        var id = row.CaseId.Value;
        fixture.Db.CaseShares.Add(new CaseShareEntity { CaseId = id, ActorId = CaseImportPostgresTests.Actor.ActorId, Version = 1, IsActive = true,
            GrantedByActorId = actorId, GrantedAtUtc = DateTimeOffset.UtcNow });
        await fixture.Db.SaveChangesAsync(); fixture.Db.ChangeTracker.Clear();
        if (classify)
        {
            var source = await fixture.Db.CaseEvents.AsNoTracking().SingleAsync(e => e.CaseId == id && e.EventType == "CaseReported");
            var registry = new ClassificationRegistry(fixture.Db, fixture.ClassificationPayloadStore(fixture.Db), Options.Create(classifier), TimeProvider.System, fixture.Protection);
            var claimed = await registry.ClaimAsync(new IngestionNotice(1, source.EventId, "case", id, source.AggregateVersion, source.ReportRevision, source.EventType, source.RecordedAtUtc), default);
            if (claimed.Lease is { } lease)
                Assert.Equal("Completed", await registry.CompleteAsync(new(lease.ResultId, lease.LeaseId, lease.ConfigurationHash, lease.Fingerprint,
                    lease.Profile.Labels.Select(l => new ClassificationPrediction(l.Id, l.Id == "respiratory" ? .9 : .1)).ToArray(), null, 10, 1), default));
        }
        return id;
    }

    private static ServiceProvider AuthorityServices(SwiftAidDbContext db) => new ServiceCollection()
        .AddScoped(_ => new SwiftAidDbContext(new DbContextOptionsBuilder<SwiftAidDbContext>().UseNpgsql(db.Database.GetConnectionString()).UseOpenIddict().Options))
        .AddScoped<PostgresCaseCommandUnitOfWork>()
        .AddScoped<ICaseCommandUnitOfWork>(s => s.GetRequiredService<PostgresCaseCommandUnitOfWork>())
        .AddScoped<ICaseCommandLeaseAccessor>(s => s.GetRequiredService<PostgresCaseCommandUnitOfWork>())
        .AddScoped<ICurrentAccessGrantStore, PostgresCurrentAccessGrantStore>()
        .AddScoped<ICaseAccessDescriptorStore, PostgresCaseAccessDescriptorStore>()
        .AddScoped<ICaseAccessPolicy, CaseAccessPolicy>().BuildServiceProvider();
}
