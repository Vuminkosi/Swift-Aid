using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Classification;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Events;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence.Classification;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class TranslationRegistryTests
{
    [IntakePostgresFact]
    public async Task ExpiredLeasesCannotOverwriteACompletedImmutableTranslation()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var context = await fixture.CreateIsolatedWorkspaceAsync();
        var imported = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), context, 1, true,
            new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
        var caseId = Assert.Single(imported.Rows).CaseId!.Value;
        var header = await fixture.Db.ManagedCases.SingleAsync(x => x.CaseId == caseId);
        var source = await fixture.Db.CaseEvents.SingleAsync(x => x.CaseId == caseId && x.EventType == "CaseReported");
        var options = Options.Create(new TranslationOptions { Enabled = true, Organizations = ["org-zw-1"] });
        var clock = new Clock();
        var environment = new CaseRegistrationBackendTests.TestHostEnvironment(Environments.Development);
        var registry = new TranslationRegistry(fixture.Db, fixture.Protection, options, environment, clock);
        await using (var transaction = await fixture.Db.Database.BeginTransactionAsync())
        {
            await registry.EnsureAsync(header, source, "2 synthetic observations", "sna", default);
            await fixture.Db.SaveChangesAsync(); await transaction.CommitAsync();
        }
        var original = (await registry.ClaimAsync(default))!;
        clock.Now = clock.Now.AddMinutes(4);
        await using var other = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var restarted = new TranslationRegistry(other, fixture.Protection, options, environment, clock);
        var claims = await Task.WhenAll(registry.ClaimAsync(default), restarted.ClaimAsync(default));
        var replacement = Assert.Single(claims, x => x is not null)!;
        var result = new EllaiTranslationResult(original.ArtifactId.ToString("N"), "2 synthetic observations", "sna", "eng",
            TranslationIdentity.Model, TranslationIdentity.Revision, 5, 1, "unreviewed");
        Assert.Equal("StaleLease", await registry.CompleteAsync(original, result, default));
        Assert.Equal("Completed", await registry.CompleteAsync(replacement, result, default));
        Assert.Equal("StaleLease", await registry.FailAsync(original, true, default));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Db.Set<TranslationArtifactEntity>().Where(x => x.Id == original.ArtifactId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProtectedTranslation, new byte[] { 1 })));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Db.Set<TranslationAssociationEntity>().Where(x => x.ArtifactId == original.ArtifactId).ExecuteDeleteAsync());
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [IntakePostgresFact]
    public async Task NativeAdmissionPersistsProtectedArtifactAndReviewReplaysIntoExistingClassifier()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var context = await fixture.CreateIsolatedWorkspaceAsync();
        var imported = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), context, 1, true,
            new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
        var caseId = Assert.Single(imported.Rows).CaseId!.Value;
        var source = await fixture.Db.CaseEvents.AsNoTracking().SingleAsync(x => x.CaseId == caseId && x.EventType == "CaseReported");
        var notice = new IngestionNotice(1, source.EventId, "case", caseId, source.AggregateVersion, source.ReportRevision, source.EventType, source.RecordedAtUtc);
        var options = new TranslationOptions { Enabled = true, Organizations = ["org-zw-1"], Reviewers = [CaseImportPostgresTests.Actor.ActorId] };
        var translation = new TranslationRegistry(fixture.Db, fixture.Protection, Options.Create(options),
            new CaseRegistrationBackendTests.TestHostEnvironment(Environments.Development), TimeProvider.System);
        var classificationOptions = new ClassificationOptions { Enabled = true, Organizations = ["org-zw-1"], WorkerTokenSha256 = new string('a', 64) };
        var classification = new ClassificationRegistry(fixture.Db, fixture.ClassificationPayloadStore(fixture.Db), Options.Create(classificationOptions),
            TimeProvider.System, fixture.Protection, translation);
        var initial = (await classification.ClaimAsync(notice, default)).Lease!;
        Assert.Equal("sn", initial.Profile.SourceLanguage);
        Assert.Null(initial.Profile.Translation);
        await classification.CompleteAsync(new(initial.ResultId, initial.LeaseId, initial.ConfigurationHash, initial.Fingerprint,
            [], "unsupported-language", 0, 0), default);
        var work = Assert.IsType<TranslationWork>(await translation.ClaimAsync(default));
        var result = new EllaiTranslationResult(work.ArtifactId.ToString("N"), "Two synthetic reports.", "sna", "eng",
            TranslationIdentity.Model, TranslationIdentity.Revision, 10, 1, "unreviewed");
        Assert.Equal("Completed", await translation.CompleteAsync(work, result, default));
        Assert.Equal("Completed", await translation.CompleteAsync(work, result, default));
        Assert.Equal("Conflict", await translation.CompleteAsync(work, result with { Text = "Changed" }, default));
        Assert.Equal("Completed", (await classification.ClaimAsync(notice, default)).State);

        var unit = new PostgresCaseCommandUnitOfWork(fixture.Db);
        var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(fixture.Db, unit), new PostgresCaseAccessDescriptorStore(fixture.Db, unit));
        var review = new CaseTranslationService(fixture.Db, policy, unit, translation, Options.Create(options), TimeProvider.System);
        var evidence = Assert.Single((await review.ReadAsync(CaseImportPostgresTests.Actor, caseId, default))!);
        Assert.Equal("Unreviewed", evidence.ReviewStatus);
        Assert.Equal(result.Text, evidence.TranslatedText);
        Assert.True(evidence.CanReview);
        var command = new TranslationReviewRequest(Guid.NewGuid(), 0, true);
        Assert.Equal("Recorded", await review.ReviewAsync(CaseImportPostgresTests.Actor, caseId, work.ArtifactId, command, default));
        Assert.Equal("Replayed", await review.ReviewAsync(CaseImportPostgresTests.Actor, caseId, work.ArtifactId, command, default));
        Assert.Equal(1, await fixture.Db.Set<TranslationOutboxEntity>().CountAsync(x => x.ArtifactId == work.ArtifactId));
        var translated = (await classification.ClaimAsync(notice, default)).Lease!;
        Assert.Equal("en", translated.Profile.SourceLanguage);
        Assert.Equal(result.Text, translated.Text);
        Assert.Equal(work.ArtifactId, translated.Profile.Translation!.ArtifactId);
        Assert.NotEqual(initial.ConfigurationHash, translated.ConfigurationHash);
        options.Reviewers = [CaseImportPostgresTests.Actor.ActorId];
        Assert.Equal("Recorded", await review.ReviewAsync(CaseImportPostgresTests.Actor, caseId, work.ArtifactId, new(Guid.NewGuid(), 1, false), default));
        Assert.Equal("Failed", await classification.CompleteAsync(new(translated.ResultId, translated.LeaseId, translated.ConfigurationHash,
            translated.Fingerprint, translated.Profile.Labels.Select(x => new ClassificationPrediction(x.Id, 0.5)).ToArray(), null, 10, 1), default));
        var protectedBytes = (await fixture.Db.Set<TranslationArtifactEntity>().FindAsync(work.ArtifactId))!.ProtectedTranslation!;
        Assert.DoesNotContain(result.Text, System.Text.Encoding.UTF8.GetString(protectedBytes));
        options.Reviewers = [];
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.ReviewAsync(CaseImportPostgresTests.Actor,
            caseId, work.ArtifactId, new(Guid.NewGuid(), 2, false), default));
        options.Enabled = false;
        Assert.False(translation.Allows("org-zw-1"));
        Assert.Null(await translation.ClaimAsync(default));
    }

    [Fact]
    public void ProductionAndUnscopedOperatorsCannotEnableExperimentalAdmission()
    {
        var options = new TranslationOptions { Enabled = true, Organizations = ["org"] };
        Assert.False(options.Allows("org", Environments.Production));
        Assert.False(options.Allows("other", Environments.Development));
        Assert.True(options.Allows("org", Environments.Development));
        options.RequireHumanReview = false;
        Assert.False(options.Allows("org", Environments.Development));
    }
}
