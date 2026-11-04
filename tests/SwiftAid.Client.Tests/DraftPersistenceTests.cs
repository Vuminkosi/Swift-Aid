using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Client.LocalStore;

namespace SwiftAid.Client.Tests;

public sealed class DraftPersistenceTests
{
    [Fact]
    public async Task SaveAndReopen_PreservesPartialHumanReportAndEncryptedEvidence()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var draft = CreateHumanDraft();
        var firstStore = fixture.CreateStore();

        await firstStore.SaveAsync(draft, CancellationToken.None);

        var reopenedStore = fixture.CreateStore();
        var loaded = await reopenedStore.GetAsync(draft.DraftId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("Draft", loaded.DraftState);
        Assert.Equal(DraftValueState.Unknown, loaded.Human!.AgeYears!.State);
        Assert.Null(loaded.Human.AgeYears.Value);
        Assert.Equal(DraftValueState.Unknown, loaded.Context!.Occurrence!.State);
        Assert.Equal(DraftValueState.Unknown, loaded.Context.CountryCode!.State);
        Assert.Equal("Reporter's exact narrative", loaded.Evidence.OriginalNarrative);
        Assert.Equal("asset://audio/one", loaded.Evidence.EvidenceReferences.Single().Reference);
        Assert.Equal(draft.Confirmations, loaded.Confirmations);

        var fileBytes = await File.ReadAllBytesAsync(fixture.DatabasePath);
        var databaseText = Encoding.UTF8.GetString(fileBytes);
        Assert.DoesNotContain("Reporter's exact narrative", databaseText);
    }

    [Fact]
    public async Task AccountSwitchAndExpiredOrMissingContext_DenyReadWithoutDeletingEncryptedRows()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var draft = CreateHumanDraft();
        await fixture.CreateStore().SaveAsync(draft, CancellationToken.None);

        fixture.Access.Context = fixture.CreateContext(actorId: "actor-b");
        Assert.Null(await fixture.CreateStore().GetAsync(draft.DraftId, CancellationToken.None));

        fixture.Access.Context = fixture.CreateContext() with { AccessEpoch = 8 };
        Assert.Null(await fixture.CreateStore().GetAsync(draft.DraftId, CancellationToken.None));

        fixture.Access.Context = null;
        await Assert.ThrowsAsync<LocalDraftAccessDeniedException>(() => fixture.CreateStore().SaveAsync(draft, CancellationToken.None));

        fixture.Access.Context = fixture.CreateContext(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        await Assert.ThrowsAsync<LocalDraftAccessDeniedException>(() => fixture.CreateStore().GetAsync(draft.DraftId, CancellationToken.None));

        Assert.Equal(1, await fixture.CountRowsAsync());
    }

    [Fact]
    public async Task AccessEpochMetadataReplay_FailsClosedAndRetainsEncryptedRow()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var draft = CreateHumanDraft();
        await fixture.CreateStore().SaveAsync(draft, CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var row = await db.Drafts.SingleAsync();
            row.AccessEpoch = 8;
            await db.SaveChangesAsync();
        }

        fixture.Access.Context = fixture.CreateContext() with { AccessEpoch = 8 };
        await Assert.ThrowsAsync<LocalDraftIntegrityException>(
            () => fixture.CreateStore().GetAsync(draft.DraftId, CancellationToken.None));
        Assert.Equal(1, await fixture.CountRowsAsync());
    }

    [Fact]
    public async Task LegacyEncryptionVersion_FailsClosedAndCannotBeSilentlyReplaced()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var draft = CreateHumanDraft();
        await fixture.CreateStore().SaveAsync(draft, CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var row = await db.Drafts.SingleAsync();
            row.SchemaVersion = EncryptedDraftRow.CurrentEncryptionVersion - 1;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<LocalDraftIntegrityException>(
            () => fixture.CreateStore().GetAsync(draft.DraftId, CancellationToken.None));
        await Assert.ThrowsAsync<LocalDraftIntegrityException>(
            () => fixture.CreateStore().SaveAsync(draft with { UpdatedAtUtc = DateTimeOffset.UtcNow }, CancellationToken.None));

        await using var verifyDb = fixture.CreateDbContext();
        var retainedRow = await verifyDb.Drafts.SingleAsync();
        Assert.Equal(EncryptedDraftRow.CurrentEncryptionVersion - 1, retainedRow.SchemaVersion);
        Assert.Equal(1, await fixture.CountRowsAsync());
    }

    [Fact]
    public async Task DifferentWorkspaceInAad_FailsClosedAndRetainsRow()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var draft = CreateHumanDraft();
        await fixture.CreateStore().SaveAsync(draft, CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var row = await db.Drafts.SingleAsync();
            row.WorkspaceId = "workspace-b";
            await db.SaveChangesAsync();
        }

        fixture.Access.Context = fixture.CreateContext(permittedWorkspaces: ["workspace-a", "workspace-b"]);
        await Assert.ThrowsAsync<LocalDraftIntegrityException>(() => fixture.CreateStore().GetAsync(draft.DraftId, CancellationToken.None));
        Assert.Equal(1, await fixture.CountRowsAsync());
    }

    [Fact]
    public async Task DifferentAccountKey_CannotDecryptAnotherAccountsRow()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var draft = CreateHumanDraft();
        await fixture.CreateStore().SaveAsync(draft, CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var row = await db.Drafts.SingleAsync();
            row.OwnerActorId = "actor-b";
            await db.SaveChangesAsync();
        }

        fixture.Access.Context = fixture.CreateContext(actorId: "actor-b");
        await Assert.ThrowsAsync<LocalDraftIntegrityException>(() => fixture.CreateStore().GetAsync(draft.DraftId, CancellationToken.None));
        Assert.Equal(1, await fixture.CountRowsAsync());
    }

    [Fact]
    public async Task CiphertextTampering_FailsClosedAndRetainsRow()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var draft = CreateAnimalDraft();
        await fixture.CreateStore().SaveAsync(draft, CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var row = await db.Drafts.SingleAsync();
            var tamperedCiphertext = row.Ciphertext.ToArray();
            tamperedCiphertext[0] ^= 0x40;
            row.Ciphertext = tamperedCiphertext;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<LocalDraftIntegrityException>(() => fixture.CreateStore().GetAsync(draft.DraftId, CancellationToken.None));
        Assert.Equal(1, await fixture.CountRowsAsync());
    }

    [Fact]
    public async Task AnimalDraftRetainsUnknownCountsAndDoesNotDependOnProjectionFile()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var draft = CreateAnimalDraft();
        await fixture.CreateStore().SaveAsync(draft, CancellationToken.None);
        var projectionPath = Path.Combine(fixture.DirectoryPath, "replaceable-projection.db");
        await File.WriteAllTextAsync(projectionPath, "synthetic projection");
        File.Delete(projectionPath);

        var loaded = await fixture.CreateStore().GetAsync(draft.DraftId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(DraftValueState.Unknown, loaded.Animal!.AnimalsAtRisk!.State);
        Assert.Null(loaded.Animal.AnimalsAtRisk.Value);
        Assert.Equal(DraftValueState.NotRecorded, loaded.Animal.AnimalsAffected!.State);
        Assert.Equal("asset://photo/original-1", loaded.Evidence.EvidenceReferences.Single().Reference);
        Assert.Equal("Draft", loaded.DraftState);
    }

    [Fact]
    public async Task SQLiteInsertFailure_NeverAcknowledgesSaveOrLeavesRow()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_local_draft_insert BEFORE INSERT ON local_report_drafts BEGIN SELECT RAISE(ABORT, 'synthetic storage failure'); END;");
        }

        var draft = CreateHumanDraft();
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.CreateStore().SaveAsync(draft, CancellationToken.None));
        Assert.Equal(0, await fixture.CountRowsAsync());
    }

    [Fact]
    public async Task MissingWorkspacePermission_DeniesSaveWithoutCreatingRow()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        fixture.Access.Context = fixture.CreateContext(permittedWorkspaces: ["another-workspace"]);

        await Assert.ThrowsAsync<LocalDraftAccessDeniedException>(() => fixture.CreateStore().SaveAsync(CreateHumanDraft(), CancellationToken.None));
        Assert.Equal(0, await fixture.CountRowsAsync());
    }

    private static ReportDraft CreateHumanDraft() => new(
        Guid.NewGuid(), ReportDraftKind.Human, "workspace-a",
        new DraftReportContext(
            new DraftFact<DraftReportedTime?>(DraftValueState.Unknown, null, new DraftProvenance(DraftReportSource.ReporterObservation, "occurrence-source")),
            null, null, null,
            new DraftFact<string?>(DraftValueState.Unknown, null, new DraftProvenance(DraftReportSource.ReporterObservation, "country-source")),
            null, null, null, null, null, null, null, null),
        new DraftEvidence(
            "Reporter's exact narrative",
            new DraftProvenance(DraftReportSource.ReporterNarrative, "narrative-1"),
            [new DraftObservation("An explicitly observed fact", new DraftProvenance(DraftReportSource.ReporterObservation, "observation-1"))],
            [new DraftEvidenceReference("asset://audio/one", new DraftProvenance(DraftReportSource.OriginalAudio, "audio-1"))]),
        new DraftConfirmations(true, "SZ", "catalogue-v1", true, true, "policy", "v1", true, "recording", "v1"),
        new HumanReportDraft(
            new DraftFact<string?>(DraftValueState.Known, "subject-ref-1", new DraftProvenance(DraftReportSource.ReporterObservation, "subject-source")),
            new DraftFact<int?>(DraftValueState.Unknown, null, new DraftProvenance(DraftReportSource.ReporterObservation, "age-source")),
            new DraftFact<string?>(DraftValueState.NotRecorded, null, new DraftProvenance(DraftReportSource.ReporterObservation, "concern-source"))),
        null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static ReportDraft CreateAnimalDraft() => new(
        Guid.NewGuid(), ReportDraftKind.Animal, "workspace-a", null,
        new DraftEvidence(null, null, [], [new DraftEvidenceReference("asset://photo/original-1", new DraftProvenance(DraftReportSource.ReporterObservation, "photo-source"))]),
        null, null,
        new AnimalReportDraft(
            DraftAnimalSubjectScope.Herd,
            new DraftFact<string?>(DraftValueState.Known, "cattle", new DraftProvenance(DraftReportSource.ReporterObservation, "species-source")),
            null,
            new DraftAnimalCount(DraftValueState.Unknown, null, new DraftProvenance(DraftReportSource.ReporterObservation, "risk-source"), null, null),
            new DraftAnimalCount(DraftValueState.NotRecorded, null, new DraftProvenance(DraftReportSource.ReporterObservation, "affected-source"), null, null),
            null),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class DraftFixture : IAsyncDisposable
    {
        private readonly byte[] _actorAKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        private DraftFixture(string directoryPath)
        {
            DirectoryPath = directoryPath;
            DatabasePath = Path.Combine(directoryPath, "local-drafts.db");
            Access = new MutableAccessContextProvider { Context = CreateContext() };
            Keys = new TestDraftKeyStore(_actorAKey);
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public MutableAccessContextProvider Access { get; }
        public TestDraftKeyStore Keys { get; }

        public static async Task<DraftFixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), "swiftaid-draft-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            await Task.Yield();
            return new DraftFixture(path);
        }

        public LocalDraftAccessContext CreateContext(
            string actorId = "actor-a",
            DateTimeOffset? expiresAt = null,
            IReadOnlyCollection<string>? permittedWorkspaces = null) =>
            new(actorId, true, true, 7, expiresAt ?? DateTimeOffset.UtcNow.AddHours(1), permittedWorkspaces ?? ["workspace-a"]);

        public LocalDraftDbContext CreateDbContext()
        {
            return new LocalDraftDbContextFactory(DatabasePath).CreateDbContext();
        }

        public SqliteDraftStore CreateStore() => new(new LocalDraftDbContextFactory(DatabasePath), Access, Keys);

        public async Task<int> CountRowsAsync()
        {
            await using var db = CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            return await db.Drafts.CountAsync();
        }

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); }
            catch (DirectoryNotFoundException) { }
            return ValueTask.CompletedTask;
        }

    }

    private sealed class MutableAccessContextProvider : ILocalDraftAccessContextProvider
    {
        public LocalDraftAccessContext? Context { get; set; }
        public ValueTask<LocalDraftAccessContext?> GetCurrentAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Context);
        }
    }

    private sealed class TestDraftKeyStore(byte[] firstKey) : ILocalDraftKeyStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal)
        {
            ["actor-a"] = firstKey.ToArray()
        };

        public ValueTask<byte[]> GetOrCreateKeyAsync(string actorId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = _keys.GetOrAdd(actorId, _ => RandomNumberGenerator.GetBytes(32));
            return ValueTask.FromResult(key.ToArray());
        }
    }
}
