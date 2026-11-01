using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Classification;
using SwiftAid.Contracts.Events;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Classification;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class ClassificationRegistryTests
{
    private static ClassificationOptions Settings() => new()
    {
        Enabled = true, WorkerTokenSha256 = new string('a', 64), Organizations = ["org-zw-1"],
        Profile = new ClassificationOptions().Profile with { SourceLanguage = "en", TaxonomyVersion = Guid.NewGuid().ToString("N") }
    };

    private static ClassificationRegistry Registry(CaseImportPostgresTests.Fixture fixture, SwiftAidDbContext? db = null,
        ClassificationOptions? options = null, TimeProvider? clock = null) => new(db ?? fixture.Db,
            fixture.ClassificationPayloadStore(db ?? fixture.Db), Options.Create(options ?? Settings()), clock ?? TimeProvider.System);

    private static async Task<IngestionNotice> ImportAsync(CaseImportPostgresTests.Fixture fixture)
    {
        var result = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), CaseImportPostgresTests.Context,
            1, true, new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
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
