using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Application.Intake;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Intake;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.IntakeImports;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Registration;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class CaseImportPostgresTests
{
    internal static readonly CaseImportContext Context = new("workspace-1",
        new IntakeConfirmations(true, "ZW", "catalogue/fixture", true, true, "fixture-policy", "1", null, null, null));
    internal static readonly ActorContext Actor = CaseRegistrationBackendTests.CreateOfficer("officer-1");

    [IntakePostgresFact]
    public async Task Real_registration_persists_and_restart_replay_does_not_duplicate_cases()
    {
        await using var fixture = await Fixture.CreateAsync();
        var importId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var csv = Csv(commandId);
        var first = await fixture.Service.ProcessAsync(Actor, importId, Context, 1, true, new StringReader(csv), default);
        Assert.Null(first.ErrorCode);
        Assert.Equal("Accepted", Assert.Single(first.Rows).Status);
        Assert.Equal("Completed", first.Summary!.State);
        Assert.Equal(2, first.Summary.NextRow);
        Assert.Equal(1, first.Summary.Accepted);
        var row = await fixture.Db.Set<CaseImportRowEntity>().AsNoTracking().SingleAsync(row => row.ImportId == importId);
        Assert.DoesNotContain("Original supplied concern", Encoding.UTF8.GetString(row.ProtectedSource));
        var original = fixture.Protection.CreateProtector("SwiftAid.CaseImport.Row.v1", Actor.ActorId, importId.ToString("N"), "1")
            .Unprotect(row.ProtectedSource);
        Assert.Contains("Original supplied concern", Encoding.UTF8.GetString(original));
        CryptographicOperations.ZeroMemory(original);
        // New context and store represent restart; the durable receipt and ledger are authoritative.
        await using var reopened = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await using (var replayLease = await new PostgresCaseRegistrationUnitOfWork(reopened)
            .BeginExistingCaseReplayAsync(Actor, first.Rows[0].CaseId!.Value, default))
        {
            Assert.True(replayLease.IsActive);
        }
        var replay = await fixture.CreateService(reopened).ProcessAsync(Actor, importId, Context, 1, true, new StringReader(csv), default);
        Assert.Equal("Replayed", Assert.Single(replay.Rows).Status);
        Assert.Equal(first.Rows[0].CaseId, replay.Rows[0].CaseId);
        Assert.Equal(1, await reopened.CommandReceipts.CountAsync(row => row.CommandId == commandId));
        Assert.Equal(2, await reopened.CaseEvents.CountAsync(row => row.CommandId == commandId));
        var eventIds = await reopened.CaseEvents.Where(row => row.CommandId == commandId).Select(row => row.EventId).ToArrayAsync();
        Assert.Equal(2, await reopened.DomainOutbox.CountAsync(row => eventIds.Contains(row.EventId)));
    }

    [IntakePostgresFact]
    public async Task Case_commit_before_checkpoint_failure_recovers_using_the_original_command()
    {
        await using var fixture = await Fixture.CreateAsync();
        var importId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var store = new FailCheckpointOnce(fixture.Store);
        var service = fixture.CreateService(fixture.Db, store);
        var first = await service.ProcessAsync(Actor, importId, Context, 1, false, new StringReader(Csv(commandId)), default);
        Assert.Equal("import-unavailable", first.ErrorCode);
        Assert.Empty(first.Rows);
        Assert.Equal(1, first.Summary!.Unresolved);
        Assert.Equal(1, await fixture.Db.CommandReceipts.CountAsync(row => row.CommandId == commandId));
        var replay = await fixture.Service.ProcessAsync(Actor, importId, Context, 1, true, new StringReader(Csv(commandId)), default);
        Assert.Equal("Replayed", Assert.Single(replay.Rows).Status);
        Assert.Equal(1, replay.Summary!.Accepted);
        Assert.Equal(0, replay.Summary.Unresolved);
        Assert.Equal(1, await fixture.Db.CommandReceipts.CountAsync(row => row.CommandId == commandId));
    }

    [IntakePostgresFact]
    public async Task Invalid_row_is_durable_and_does_not_roll_back_a_later_valid_row()
    {
        await using var fixture = await Fixture.CreateAsync();
        var csv = Csv(Guid.NewGuid(), observations: "[{}]") + Csv(Guid.NewGuid()).Split('\n', 2)[1];
        var result = await fixture.Service.ProcessAsync(Actor, Guid.NewGuid(), Context, 1, true, new StringReader(csv), default);
        Assert.Equal(new[] { "Invalid", "Accepted" }, result.Rows.Select(row => row.Status));
        Assert.Equal(1, result.Summary!.Rejected);
        Assert.Equal(1, result.Summary.Accepted);
        Assert.Equal(3, result.Summary.NextRow);
        var restored = await fixture.Store.GetIssuesAsync(Actor, result.ImportId, 0, default);
        Assert.Equal("Invalid", Assert.Single(restored).Status);
        Assert.Empty(await fixture.Store.GetIssuesAsync(Actor, result.ImportId, 1, default));
        Assert.Empty(await fixture.Store.GetIssuesAsync(CaseRegistrationBackendTests.CreateOfficer("another-actor"), result.ImportId, 0, default));
    }

    [IntakePostgresFact]
    public async Task Immutable_row_content_checkpoint_gaps_and_active_leases_are_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        var lease = await fixture.Store.OpenAsync(Actor, id, Context, 1, default);
        await using var otherDb = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var other = new PostgresCaseImportStore(otherDb, fixture.Protection, TimeProvider.System);
        var busy = await Assert.ThrowsAsync<CaseImportException>(() => other.OpenAsync(Actor, id, Context, 1, default));
        Assert.Equal("import-busy", busy.Code);
        await fixture.Store.BeginRowAsync(lease, 1, null, SHA256.HashData([1]), [1], default);
        var changed = await Assert.ThrowsAsync<CaseImportException>(() => fixture.Store.BeginRowAsync(lease, 1, null, SHA256.HashData([2]), [2], default));
        Assert.Equal("row-content-conflict", changed.Code);
        fixture.Db.ChangeTracker.Clear();
        await fixture.Store.CloseAsync(lease, "Paused", null, default);
        otherDb.ChangeTracker.Clear();
        var gap = await Assert.ThrowsAsync<CaseImportException>(() => other.OpenAsync(Actor, id, Context, 10, default));
        Assert.Equal("checkpoint-gap", gap.Code);
    }

    [IntakePostgresFact]
    public async Task Owner_scope_and_current_access_epoch_are_required_for_history()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        var lease = await fixture.Store.OpenAsync(Actor, id, Context, 1, default);
        await fixture.Store.CloseAsync(lease, "Paused", null, default);
        Assert.Null(await fixture.Store.GetAsync(CaseRegistrationBackendTests.CreateOfficer("another-actor"), id, default));
        await fixture.Db.ActorAccessStates.Where(row => row.ActorId == Actor.ActorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AccessEpoch, Actor.AccessEpoch + 1));
        var denied = await Assert.ThrowsAsync<CaseImportException>(() => fixture.Store.GetAsync(Actor, id, default));
        Assert.Equal("not-authorized", denied.Code);
    }

    internal static string Csv(Guid commandId, string observations = "[\"Cough reported\"]") =>
        string.Join(',', CaseCsvImportAdapter.Columns) + "\n" +
        string.Join(',', new[] { "1", commandId.ToString("D"), Guid.Empty.ToString("D"), "SYNTHETIC-1", "Human",
            "Original supplied concern", observations, "2026-06-04T16:46:59+02:00", "2026-06-02T21:51:59+02:00", "ZW", "Manicaland", "", "Rusape", "ZW:place:rusape", "", "", "Approximate", "", "", "sn", "SYNTHETIC:SWIFTAID:1" }
            .Select((value, index) => index == 2 ? "" : "\"" + value.Replace("\"", "\"\"") + "\"")) + "\n";

    [IntakePostgresFact]
    public async Task Workspace_destinations_and_recent_imports_follow_current_owner_scope()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Contains(await fixture.Store.GetWorkspacesAsync(Actor, default),
            scope => scope.WorkspaceId == Context.WorkspaceId && scope.CountryCode == "ZW");
        Assert.Empty(await fixture.Store.GetWorkspacesAsync(CaseRegistrationBackendTests.CreateOfficer("another-actor"), default));
        var id = Guid.NewGuid();
        await fixture.Service.ProcessAsync(Actor, id, Context, 1, true, new StringReader(Csv(Guid.NewGuid())), default);
        var history = await fixture.Store.GetRecentAsync(Actor, Context.WorkspaceId, "ZW", default);
        Assert.Contains(history, item => item.ImportId == id && item.State == "Completed");
        Assert.DoesNotContain(await fixture.Store.GetWorkspacesAsync(Actor, default), scope => scope.CountryCode == "SZ");
        await Assert.ThrowsAsync<CaseImportException>(() => fixture.Store.GetRecentAsync(Actor, "another-workspace", "ZW", default));
        await fixture.Db.ActorAccessStates.Where(row => row.ActorId == Actor.ActorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AccessEpoch, Actor.AccessEpoch + 1));
        Assert.Empty(await fixture.Store.GetWorkspacesAsync(Actor, default));
        await Assert.ThrowsAsync<CaseImportException>(() => fixture.Store.GetRecentAsync(Actor, Context.WorkspaceId, "ZW", default));
    }

    [IntakePostgresFact]
    public async Task Completed_import_is_sealed_and_denied_replay_cannot_erase_saved_case()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        var command = Guid.NewGuid();
        await fixture.Service.ProcessAsync(Actor, id, Context, 1, true, new StringReader(Csv(command)), default);
        var append = await Assert.ThrowsAsync<CaseImportException>(() => fixture.Store.OpenAsync(Actor, id, Context, 2, default));
        Assert.Equal("import-completed", append.Code);
        var lease = await fixture.Store.OpenAsync(Actor, id, Context, 1, default);
        await fixture.Store.CompleteRowAsync(lease, new(1, command, "Denied", null, "registration-denied"), default);
        await fixture.Store.CloseAsync(lease, "Paused", "registration-denied", default);
        var status = await fixture.Store.GetAsync(Actor, id, default);
        Assert.Equal("Completed", status!.State);
        Assert.Equal(1, status.Accepted);
        Assert.Equal(0, status.Rejected);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly CaseRegistrationBackendTests.SyntheticRegistrationKeyRing _keys;
        public SwiftAidDbContext Db { get; }
        public ProtectedPayloadDataProtectionProvider Protection { get; }
        public PostgresCaseImportStore Store { get; }
        public CaseCsvImportService Service { get; }

        private Fixture(SwiftAidDbContext db, CaseRegistrationBackendTests.SyntheticRegistrationKeyRing keys)
        {
            Db = db;
            _keys = keys;
            Protection = ProtectedPayloadDataProtectionProviderFactory.Create(keys.CreateOptions(), new CaseRegistrationBackendTests.TestHostEnvironment(Environments.Production));
            Store = new(db, Protection, TimeProvider.System);
            Service = CreateService(db, Store);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
            if (!await db.Actors.AnyAsync(row => row.ActorId == "granter-1"))
            {
                db.Actors.Add(new ActorEntity { ActorId = "granter-1", Status = PersistentActorStatus.Active,
                    CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }
            await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);
            return new(db, CaseRegistrationBackendTests.SyntheticRegistrationKeyRing.Create());
        }

        public CaseCsvImportService CreateService(SwiftAidDbContext db, ICaseImportStore? store = null, IIntakeSubmissionValidator? validator = null) => new(
            store ?? new PostgresCaseImportStore(db, Protection, TimeProvider.System),
            new CaseRegistrationProcessor(new PostgresCaseRegistrationUnitOfWork(db), new PostgresCaseRegistrationAuthorization(db),
                CaseRegistrationBackendTests.CreatePersistence(db, _keys.CreateOptions(), Protection)), validator ?? new FixtureValidator());

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); Protection.Dispose(); _keys.Dispose(); }
    }

    internal sealed class FixtureValidator : IIntakeSubmissionValidator
    {
        public Task<bool> ValidateAsync(IntakeSubmission submission, IntakeLimits limits, CancellationToken ct)
        { IntakeSubmissionRules.Validate(submission, limits); return Task.FromResult(submission.Location.CountryCode == "ZW"); }
    }

    [IntakePostgresFact]
    public async Task Validation_dependency_failure_is_durable_bounded_and_recoverable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var importId = Guid.NewGuid();
        var command = Guid.NewGuid();
        var service = fixture.CreateService(fixture.Db, validator: new UnavailableValidator());
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var result = await service.ProcessAsync(Actor, importId, Context, 1, true, new StringReader(Csv(command)), default);
            var row = Assert.Single(result.Rows);
            Assert.Equal(attempt == 5 ? "Failed" : "RetryableFailure", row.Status);
            Assert.Equal("validation-unavailable", row.ErrorCode);
            Assert.Equal(attempt == 5 ? 2 : 1, result.Summary!.NextRow);
            Assert.Equal(0, result.Summary.Unresolved);
        }
        Assert.False(await fixture.Db.CommandReceipts.AnyAsync(row => row.CommandId == command));
        var ledger = await fixture.Db.Set<CaseImportRowEntity>().AsNoTracking().SingleAsync(row => row.ImportId == importId);
        Assert.Equal(5, ledger.Attempts);
        Assert.Equal("Failed", ledger.State);
        // Explicit reprocessing after the dependency recovers retains the original command identity.
        var recovered = await fixture.Service.ProcessAsync(Actor, importId, Context, 1, true, new StringReader(Csv(command)), default);
        Assert.Equal("Accepted", Assert.Single(recovered.Rows).Status);
        Assert.Equal(1, await fixture.Db.CommandReceipts.CountAsync(row => row.CommandId == command));
    }

    private sealed class UnavailableValidator : IIntakeSubmissionValidator
    {
        public Task<bool> ValidateAsync(IntakeSubmission submission, IntakeLimits limits, CancellationToken ct) =>
            throw new HttpRequestException("Synthetic policy dependency outage.");
    }

    [IntakePostgresFact]
    public async Task Registration_outages_have_a_stable_failure_code_and_five_attempt_limit()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var throws in new[] { false, true })
        {
            var service = new CaseCsvImportService(fixture.Store, new UnavailableRegistration(throws), new FixtureValidator());
            var importId = Guid.NewGuid();
            var csv = Csv(Guid.NewGuid());
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var result = await service.ProcessAsync(Actor, importId, Context, 1, true, new StringReader(csv), default);
                var row = Assert.Single(result.Rows);
                Assert.Equal(attempt == 5 ? "Failed" : "RetryableFailure", row.Status);
                Assert.Equal("registration-unavailable", row.ErrorCode);
                Assert.Equal(0, result.Summary!.Unresolved);
                Assert.Equal(attempt == 5 ? 2 : 1, result.Summary.NextRow);
            }
        }
    }

    private sealed class UnavailableRegistration(bool throws) : IManagedCaseRegistrationProcessor
    {
        public ValueTask<CaseRegistrationResult> RegisterAsync(ActorContext actor, RegisterManagedCaseRequest request, CancellationToken ct) =>
            throws ? throw new IOException("Synthetic registration outage.") :
            ValueTask.FromResult(CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Unavailable, request.CommandId));
    }

    private sealed class FailCheckpointOnce(ICaseImportStore inner) : ICaseImportStore
    {
        public Task<CaseImportLease> OpenAsync(ActorContext actor, Guid id, CaseImportContext context, long row, CancellationToken ct) => inner.OpenAsync(actor, id, context, row, ct);
        public Task<int> BeginRowAsync(CaseImportLease lease, long row, Guid? command, byte[] hash, byte[] bytes, CancellationToken ct) => inner.BeginRowAsync(lease, row, command, hash, bytes, ct);
        public Task CompleteRowAsync(CaseImportLease lease, CaseImportRowOutcome result, CancellationToken ct) => throw new IOException("Synthetic checkpoint loss after case commit.");
        public Task CloseAsync(CaseImportLease lease, string state, string? error, CancellationToken ct) => inner.CloseAsync(lease, state, error, ct);
        public Task<CaseImportSummary?> GetAsync(ActorContext actor, Guid id, CancellationToken ct) => inner.GetAsync(actor, id, ct);
    }
}
