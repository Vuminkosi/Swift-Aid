using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

[Collection(CaseAuthorityPostgresCollection.Name)]
public sealed partial class CaseCommandProcessingPostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task Collaborator_grant_and_revocation_persist_bounded_access_and_epochs_atomically()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync();
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        await AddOperationAsync(fixture, CaseOperation.ManageCollaborators);
        var recipientGroup = await AddEligibleTargetGrantAsync(fixture);
        cleanup.TrackTargetGrant(recipientGroup);
        await SetInitialWorkflowVersionAsync(fixture);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), provider);
        var grant = new GrantCaseCollaboratorCommand(Guid.NewGuid(), 2, Guid.NewGuid(), fixture.NewOwnerActorId,
            CaseCollaborationActions.ReadCase, DateTimeOffset.UtcNow.AddHours(1), "Synthetic protected grant reason",
            null, DateTimeOffset.UnixEpoch);
        async Task<CaseCommandProcessingResult> Process(CaseCommand command) =>
            await ProcessCommandAsync(fixture, fixture.CreateActor(), command, keyRing.CreateOptions(), provider);
        async Task<bool> CanRead(long epoch)
        {
            var recipient = ActorContext.FromValidatedPrincipal(new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity([
                    new System.Security.Claims.Claim("sub", fixture.NewOwnerActorId),
                    new System.Security.Claims.Claim(ActorContext.AccessEpochClaimType, epoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
                ], "fixture-authenticated")));
            await using var db = fixture.CreateDbContext();
            var unit = new PostgresCaseCommandUnitOfWork(db);
            await using var lease = await unit.BeginCaseAsync(recipient, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
            return (await policy.AuthorizeAsync(recipient, fixture.CaseId, CaseOperation.ReadCase, CancellationToken.None)).IsAllowed;
        }

        Assert.False(await CanRead(fixture.AccessEpoch));
        await using (var context = fixture.CreateDbContext())
        {
            var unit = new PostgresCaseCommandUnitOfWork(context);
            var payloads = new RecordingProtectedPayloadStore(
                new PostgresProtectedCaseEventPayloadStore(context, unit, keyRing.CreateOptions(), provider),
                async cancellationToken =>
                {
                    // Transaction-local fault injection proves the post-preparation eligibility check and rollback.
                    await context.AccessGrantOperations.Where(item => item.GrantGroupId == recipientGroup)
                        .ExecuteDeleteAsync(cancellationToken);
                });
            var persistence = new PostgresCaseCommandPersistence(context, unit, payloads, new CaseEventSerializer());
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(context, unit), new PostgresCaseAccessDescriptorStore(context, unit));
            var denied = await new CaseCommandProcessor(unit, policy, persistence)
                .ProcessAsync(fixture.CreateActor(), fixture.CaseId, 1, grant, CancellationToken.None);
            Assert.Equal(CaseCommandProcessingStatus.Denied, denied.Status);
            Assert.True(payloads.StageCompleted);
            await using var check = fixture.CreateDbContext();
            Assert.False(await check.CaseShares.AnyAsync(item => item.CaseId == fixture.CaseId));
            Assert.False(await check.CaseEvents.AnyAsync(item => item.CommandId == grant.CommandId));
            Assert.False(await check.CommandReceipts.AnyAsync(item => item.CommandId == grant.CommandId));
            Assert.False(await check.ProtectedEventPayloads.AnyAsync(item => payloads.PayloadIds.Contains(item.PayloadId)));
            Assert.Equal(2, (await check.ManagedCases.SingleAsync(item => item.CaseId == fixture.CaseId)).AggregateVersion);
            Assert.Equal(fixture.AccessEpoch, (await check.ActorAccessStates.SingleAsync(item => item.ActorId == fixture.NewOwnerActorId)).AccessEpoch);
        }

        var accepted = await Process(grant);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, accepted.Status);
        Assert.Equal(CaseCommandProcessingStatus.Replayed, (await Process(grant)).Status);
        Assert.False(await CanRead(fixture.AccessEpoch));
        Assert.True(await CanRead(fixture.AccessEpoch + 1));
        await using (var check = fixture.CreateDbContext())
        {
            var share = await check.CaseShares.SingleAsync(item => item.CaseId == fixture.CaseId && item.ActorId == fixture.NewOwnerActorId);
            Assert.Equal(grant.GrantId, share.GrantId);
            Assert.True(share.IsActive);
            Assert.Equal([nameof(CaseOperation.ReadCase)], share.AllowedOperations!);
            Assert.Null(share.ChangeReason);
            Assert.Equal(fixture.AccessEpoch + 1, (await check.ActorAccessStates.SingleAsync(item => item.ActorId == fixture.NewOwnerActorId)).AccessEpoch);
            var rollback = await Assert.ThrowsAsync<PostgresException>(() => check.GetService<IMigrator>()
                .MigrateAsync("20261102181615_BoundedCaseShareOperations"));
            Assert.Equal(PostgresErrorCodes.RaiseException, rollback.SqlState);
            Assert.Contains("Cannot remove collaborator grant identity", rollback.MessageText);
        }

        var revoke = new RevokeCaseCollaboratorCommand(Guid.NewGuid(), 3, grant.GrantId, "Synthetic protected revoke reason", null, DateTimeOffset.UnixEpoch);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, (await Process(revoke)).Status);
        Assert.Equal(CaseCommandProcessingStatus.Replayed, (await Process(revoke)).Status);
        Assert.False(await CanRead(fixture.AccessEpoch + 1));
        Assert.False(await CanRead(fixture.AccessEpoch + 2));
        await using var verification = fixture.CreateDbContext();
        var revoked = await verification.CaseShares.SingleAsync(item => item.CaseId == fixture.CaseId && item.ActorId == fixture.NewOwnerActorId);
        Assert.False(revoked.IsActive);
        Assert.Equal(2, revoked.Version);
        Assert.Equal(fixture.ActorId, revoked.RevokedByActorId);
        Assert.Null(revoked.ChangeReason);
        Assert.Equal(fixture.AccessEpoch + 2, (await verification.ActorAccessStates.SingleAsync(item => item.ActorId == fixture.NewOwnerActorId)).AccessEpoch);
        var projection = await verification.ManagedCases.SingleAsync(item => item.CaseId == fixture.CaseId);
        Assert.Equal(4, projection.AggregateVersion);
        Assert.Equal(fixture.ActorId, projection.AccountableOwnerActorId);
        var events = await verification.CaseEvents.Where(item => item.CommandId == grant.CommandId || item.CommandId == revoke.CommandId).ToArrayAsync();
        Assert.Equal(2, events.Length);
        var eventIds = events.Select(item => item.EventId).ToArray();
        Assert.Equal(2, await verification.DomainOutbox.CountAsync(item => eventIds.Contains(item.EventId)));
        Assert.Equal(2, await verification.CommandReceipts.CountAsync(item => item.CommandId == grant.CommandId || item.CommandId == revoke.CommandId));
        var aggregate = await ReadAggregateAsync(fixture, fixture.CreateActor(), keyRing.CreateOptions(), provider);
        Assert.NotNull(Assert.Single(aggregate.Collaborators).RevokedAtUtc);
    }

    [CaseAuthorityPostgresFact]
    public async Task Collaborator_eligibility_requires_locked_current_scope_and_all_actions()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(makeCurrentActorOwner: false);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await using var context = fixture.CreateDbContext();
        var unit = new PostgresCaseCommandUnitOfWork(context);
        var payloads = new PostgresProtectedCaseEventPayloadStore(context, unit, keyRing.CreateOptions(), provider);
        var persistence = new PostgresCaseCommandPersistence(context, unit, payloads, new CaseEventSerializer());
        await using var lease = await unit.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId, [], CaseLeaseMode.Write, CancellationToken.None);
        var header = Assert.IsType<CaseCommandCaseHeader>(await persistence.ReadCaseHeaderAsync(lease, CancellationToken.None));
        async Task<bool> Eligible(CaseCollaborationActions actions) => await persistence.IsEligibleCollaboratorAsync(
            lease, fixture.ActorId, actions, header, CancellationToken.None);

        // Eligibility is prospective: this actor has neither ownership nor a share.
        Assert.True(await Eligible(CaseCollaborationActions.ReadCase));
        var requested = CaseCollaborationActions.ReadCase | CaseCollaborationActions.ManageFollowUp;
        Assert.False(await Eligible(requested));
        var group = await context.AccessGrantGroups.SingleAsync(item => item.GrantGroupId == fixture.GrantGroupId);
        var separateGroup = (AccessGrantGroupEntity)context.Entry(group).CurrentValues.ToObject();
        separateGroup.GrantGroupId = $"split-{Guid.NewGuid():N}";
        context.AccessGrantGroups.Add(separateGroup);
        context.AccessGrantOperations.Add(new AccessGrantOperationEntity
        {
            GrantGroupId = separateGroup.GrantGroupId, Operation = CaseOperation.ManageFollowUp
        });
        await context.SaveChangesAsync();
        Assert.False(await Eligible(requested)); // Actions from separate groups cannot be combined.
        context.AccessGrantOperations.Add(new AccessGrantOperationEntity
        {
            GrantGroupId = fixture.GrantGroupId, Operation = CaseOperation.ManageFollowUp
        });
        await context.SaveChangesAsync();
        Assert.True(await Eligible(requested));

        var originalSector = group.Sector;
        group.Sector = Sector.AnimalHealth;
        await context.SaveChangesAsync();
        Assert.False(await Eligible(requested));
        group.Sector = originalSector;
        var originalSensitivity = group.SensitivityCode;
        group.SensitivityCode = "sens-other";
        await context.SaveChangesAsync();
        Assert.False(await Eligible(requested));
        group.SensitivityCode = originalSensitivity;
        var originalCountry = group.CountryCode;
        group.CountryCode = "ZA";
        await context.SaveChangesAsync();
        Assert.False(await Eligible(requested));
        group.CountryCode = originalCountry;
        var originalExpiry = group.ExpiresAtUtc;
        group.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await context.SaveChangesAsync();
        Assert.False(await Eligible(requested));
        group.ExpiresAtUtc = originalExpiry;
        await context.SaveChangesAsync();
        Assert.True(await Eligible(requested));
        Assert.False(await persistence.IsEligibleCollaboratorAsync(lease, fixture.ActorId, requested,
            header with { ResourceVersion = header.ResourceVersion + 1 }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Eligible(CaseCollaborationActions.ManageFollowUp));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await persistence.IsEligibleCollaboratorAsync(lease, "unlocked-recipient", requested, header, CancellationToken.None));
        // Disposal rolls back the synthetic scope mutations.
    }

    [CaseAuthorityPostgresFact]
    public async Task Bounded_share_final_write_guard_rolls_back_narrowed_scope_and_accepts_permitted_retry()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(makeCurrentActorOwner: false, createActiveShare: true);
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        await AddOperationAsync(fixture, CaseOperation.ManageFollowUp);
        await SetInitialWorkflowVersionAsync(fixture);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), provider);
        await using (var setup = fixture.CreateDbContext())
        {
            var share = await setup.CaseShares.SingleAsync(item => item.CaseId == fixture.CaseId && item.ActorId == fixture.ActorId);
            share.AllowedOperations = [nameof(CaseOperation.ReadCase), nameof(CaseOperation.ManageFollowUp)];
            await setup.SaveChangesAsync();
        }

        var command = new ScheduleFollowUpCommand(Guid.NewGuid(), 2, Guid.NewGuid(),
            "Synthetic shared follow-up", fixture.ActorId, DateTimeOffset.UtcNow.AddDays(1),
            "Synthetic bounded share", null, DateTimeOffset.UtcNow);
        await using (var context = fixture.CreateDbContext())
        {
            var unit = new PostgresCaseCommandUnitOfWork(context);
            var payloads = new RecordingProtectedPayloadStore(
                new PostgresProtectedCaseEventPayloadStore(context, unit, keyRing.CreateOptions(), provider),
                async cancellationToken =>
                {
                    // Fault injection in the lease transaction isolates the final SQL guard from the earlier policy check.
                    await context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE case_shares SET allowed_operations = ARRAY['ReadCase']::text[] WHERE case_id = {fixture.CaseId} AND actor_id = {fixture.ActorId}",
                        cancellationToken);
                });
            var persistence = new PostgresCaseCommandPersistence(context, unit, payloads, new CaseEventSerializer());
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(context, unit),
                new PostgresCaseAccessDescriptorStore(context, unit));
            var result = await new CaseCommandProcessor(unit, policy, persistence)
                .ProcessAsync(fixture.CreateActor(), fixture.CaseId, 1, command, CancellationToken.None);
            Assert.Equal(CaseCommandProcessingStatus.Denied, result.Status);
            Assert.True(payloads.StageCompleted);
            Assert.Single(payloads.PayloadIds);

            await using var verification = fixture.CreateDbContext();
            Assert.False(await verification.ProtectedEventPayloads.AnyAsync(item => payloads.PayloadIds.Contains(item.PayloadId)));
            Assert.False(await verification.CaseEvents.AnyAsync(item => item.CommandId == command.CommandId));
            Assert.False(await verification.CommandReceipts.AnyAsync(item => item.CommandId == command.CommandId));
            Assert.False(await verification.ManagedCaseFollowUps.AnyAsync(item => item.CaseId == fixture.CaseId));
            Assert.False(await verification.SecurityAuditEntries.AnyAsync(item =>
                item.ResourceId == fixture.CaseId && item.Operation == "CaseCommand.ScheduleFollowUp"));
            var projection = await verification.ManagedCases.SingleAsync(item => item.CaseId == fixture.CaseId);
            Assert.Equal(2, projection.AggregateVersion);
            Assert.Equal(fixture.OwnerActorId, projection.AccountableOwnerActorId);
            var restoredShare = await verification.CaseShares.SingleAsync(item => item.CaseId == fixture.CaseId && item.ActorId == fixture.ActorId);
            Assert.Contains(nameof(CaseOperation.ManageFollowUp), restoredShare.AllowedOperations!);
        }

        var accepted = await ProcessCommandAsync(fixture, fixture.CreateActor(), command, keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, accepted.Status);
        var replayed = await ProcessCommandAsync(fixture, fixture.CreateActor(), command, keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Replayed, replayed.Status);
        Assert.Equal(accepted.EventIds.ToArray(), replayed.EventIds.ToArray());
        await using var final = fixture.CreateDbContext();
        Assert.Single(await final.ManagedCaseFollowUps.Where(item => item.CaseId == fixture.CaseId).ToArrayAsync());
        var persistedEvent = Assert.Single(await final.CaseEvents.Where(item => item.CommandId == command.CommandId).ToArrayAsync());
        Assert.Single(await final.CommandReceipts.Where(item => item.CommandId == command.CommandId).ToArrayAsync());
        Assert.Single(await final.DomainOutbox.Where(item => item.EventId == persistedEvent.EventId).ToArrayAsync());
    }

    [CaseAuthorityPostgresFact]
    public async Task Assignment_acknowledgement_persists_and_replays_after_context_restart()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync();
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        await SetInitialWorkflowVersionAsync(fixture);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), provider);
        var command = new AcknowledgeAssignmentCommand(Guid.NewGuid(), 2, 2, null, DateTimeOffset.UnixEpoch);

        async Task<SwiftAid.Contracts.Cases.CaseTimelineHeader> ReadHeaderAsync()
        {
            await using var context = fixture.CreateDbContext();
            var unitOfWork = new PostgresCaseCommandUnitOfWork(context);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(context, unitOfWork),
                new PostgresCaseAccessDescriptorStore(context, unitOfWork));
            var query = new CaseTimelineQuery(new PostgresCaseTimelineStore(context), policy, unitOfWork);
            var result = await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 1, CancellationToken.None);
            Assert.Equal(SwiftAid.Contracts.Cases.CaseTimelineAccess.Found, result.Access);
            return Assert.IsType<SwiftAid.Contracts.Cases.CaseTimeline>(result.Timeline).Header;
        }

        async Task<CaseCommandProcessingResult> ProcessAsync(CaseCommand input)
        {
            await using var context = fixture.CreateDbContext();
            var unitOfWork = new PostgresCaseCommandUnitOfWork(context);
            var store = new PostgresProtectedCaseEventPayloadStore(context, unitOfWork, keyRing.CreateOptions(), provider);
            var persistence = new PostgresCaseCommandPersistence(context, unitOfWork, store, new CaseEventSerializer());
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(context, unitOfWork),
                new PostgresCaseAccessDescriptorStore(context, unitOfWork));
            return await new CaseCommandProcessor(unitOfWork, policy, persistence)
                .ProcessAsync(fixture.CreateActor(), fixture.CaseId, 1, input, CancellationToken.None);
        }

        var withoutGrant = await ReadHeaderAsync();
        Assert.Equal(2, withoutGrant.AssignmentVersion);
        Assert.NotNull(withoutGrant.AssignmentRecordedAtUtc);
        Assert.Null(withoutGrant.AssignmentAcknowledgedAtUtc);
        Assert.False(withoutGrant.CanAcknowledgeAssignment);
        Assert.Equal(CaseCommandProcessingStatus.Denied, (await ProcessAsync(command)).Status);

        await AddOperationAsync(fixture, CaseOperation.AcknowledgeAssignment);
        Assert.True((await ReadHeaderAsync()).CanAcknowledgeAssignment);
        await using (var revokePendingContext = fixture.CreateDbContext())
        {
            await revokePendingContext.AccessGrantOperations
                .Where(item => item.GrantGroupId == fixture.GrantGroupId && item.Operation == CaseOperation.AcknowledgeAssignment)
                .ExecuteDeleteAsync();
        }

        var revokedPending = await ReadHeaderAsync();
        Assert.False(revokedPending.CanAcknowledgeAssignment);
        Assert.Null(revokedPending.AssignmentAcknowledgedAtUtc);
        Assert.Equal(CaseCommandProcessingStatus.Denied, (await ProcessAsync(command)).Status);
        await AddOperationAsync(fixture, CaseOperation.AcknowledgeAssignment);
        var accepted = await ProcessAsync(command);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, accepted.Status);
        Assert.Equal(3, accepted.AggregateVersion);
        var acknowledgedHeader = await ReadHeaderAsync();
        Assert.Equal(3, acknowledgedHeader.AggregateVersion);
        Assert.Equal(2, acknowledgedHeader.AssignmentVersion);
        Assert.NotNull(acknowledgedHeader.AssignmentAcknowledgedAtUtc);
        Assert.False(acknowledgedHeader.CanAcknowledgeAssignment);
        var replayed = await ProcessAsync(command);
        Assert.Equal(CaseCommandProcessingStatus.Replayed, replayed.Status);
        Assert.Equal(accepted.EventIds.ToArray(), replayed.EventIds.ToArray());

        var duplicate = await ProcessAsync(command with { CommandId = Guid.NewGuid(), ExpectedVersion = 3 });
        Assert.Equal(CaseCommandProcessingStatus.Conflict, duplicate.Status);

        await using (var revokeContext = fixture.CreateDbContext())
        {
            await revokeContext.AccessGrantOperations
                .Where(item => item.GrantGroupId == fixture.GrantGroupId && item.Operation == CaseOperation.AcknowledgeAssignment)
                .ExecuteDeleteAsync();
        }

        var deniedReplay = await ProcessAsync(command);
        Assert.Equal(CaseCommandProcessingStatus.Denied, deniedReplay.Status);
        Assert.False((await ReadHeaderAsync()).CanAcknowledgeAssignment);

        await using var verification = fixture.CreateDbContext();
        var row = await verification.CaseEvents.AsNoTracking().SingleAsync(item => item.CommandId == command.CommandId);
        Assert.Equal("CaseAssignmentAcknowledged", row.EventType);
        Assert.True(row.RecordedAtUtc > command.RecordedAtUtc);
        Assert.Equal(row.RecordedAtUtc, acknowledgedHeader.AssignmentAcknowledgedAtUtc);
        Assert.Single(await verification.CommandReceipts.Where(item => item.CommandId == command.CommandId).ToArrayAsync());
        Assert.Single(await verification.DomainOutbox.Where(item => item.EventId == row.EventId).ToArrayAsync());
        var projection = await verification.ManagedCases.AsNoTracking().SingleAsync(item => item.CaseId == fixture.CaseId);
        Assert.Equal(3, projection.AggregateVersion);
        Assert.Equal(fixture.ActorId, projection.AccountableOwnerActorId);

        var replayUnit = new PostgresCaseCommandUnitOfWork(verification);
        var replayStore = new PostgresProtectedCaseEventPayloadStore(verification, replayUnit, keyRing.CreateOptions(), provider);
        var replayPersistence = new PostgresCaseCommandPersistence(verification, replayUnit, replayStore, new CaseEventSerializer());
        await using var lease = await replayUnit.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None);
        var aggregate = await replayPersistence.ReplayCaseAsync(lease, CancellationToken.None);
        Assert.Equal(2, aggregate.AssignmentVersion);
        Assert.Equal(row.RecordedAtUtc, aggregate.AssignmentAcknowledgedAtUtc);
    }

    [CaseAuthorityPostgresFact]
    public async Task Accepted_command_atomically_updates_projection_journal_receipt_audit_and_outbox()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        await AddOperationAsync(fixture, CaseOperation.ResolveCase).ConfigureAwait(false);
        await SetInitialWorkflowVersionAsync(fixture).ConfigureAwait(false);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), provider).ConfigureAwait(false);

        var commandId = Guid.NewGuid();
        var command = new ResolveCaseCommand(
            commandId,
            ExpectedVersion: 2,
            Reason: "synthetic integration test",
            OccurredAtUtc: null,
            RecordedAtUtc: DateTimeOffset.UtcNow);

        await using var dbContext = fixture.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        var payloadStore = new PostgresProtectedCaseEventPayloadStore(
            dbContext, unitOfWork, keyRing.CreateOptions(), provider);
        var persistence = new PostgresCaseCommandPersistence(
            dbContext, unitOfWork, payloadStore, new CaseEventSerializer());
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(dbContext, unitOfWork),
            new PostgresCaseAccessDescriptorStore(dbContext, unitOfWork));
        var processor = new CaseCommandProcessor(unitOfWork, policy, persistence);

        var result = await processor.ProcessAsync(
            fixture.CreateActor(), fixture.CaseId, 1, command, CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(CaseCommandProcessingStatus.Accepted, result.Status);
        Assert.Equal(3, result.AggregateVersion);
        Assert.Equal(1, result.ReportRevision);
        Assert.Single(result.EventIds);

        var projection = await dbContext.ManagedCases.AsNoTracking()
            .SingleAsync(entity => entity.CaseId == fixture.CaseId).ConfigureAwait(false);
        Assert.Equal(CaseWorkflow.Resolved, projection.Workflow);
        Assert.Equal(3, projection.AggregateVersion);
        Assert.Equal(1, projection.ReportRevision);
        Assert.Equal(2, projection.ResourceVersion);
        Assert.Equal(ClinicalClassification.Unclassified, projection.Classification);
        Assert.Equal(LaboratoryState.NotRecorded, projection.Laboratory);
        Assert.Equal(ReportedValueState.Unknown, projection.OutcomeState);
        Assert.Equal("sens-A", projection.SensitivityCode);

        var receipt = await dbContext.CommandReceipts.AsNoTracking()
            .SingleAsync(entity => entity.ActorId == fixture.ActorId && entity.CommandId == commandId)
            .ConfigureAwait(false);
        Assert.Equal(fixture.CaseId, receipt.CaseId);
        Assert.Equal(1, receipt.SchemaVersion);
        Assert.Equal(32, receipt.CanonicalPayloadHash.Length);

        var eventRow = await dbContext.CaseEvents.AsNoTracking()
            .SingleAsync(entity => entity.CommandId == commandId && entity.CaseId == fixture.CaseId)
            .ConfigureAwait(false);
        Assert.Equal("CaseResolved", eventRow.EventType);
        var payload = await dbContext.ProtectedEventPayloads.AsNoTracking()
            .SingleAsync(entity => entity.PayloadId == eventRow.PayloadId).ConfigureAwait(false);
        Assert.DoesNotContain(command.Reason, Encoding.UTF8.GetString(payload.ProtectedBytes));
        Assert.Equal(eventRow.EventId, await dbContext.DomainOutbox.AsNoTracking()
            .Where(entity => entity.EventId == eventRow.EventId)
            .Select(entity => entity.EventId)
            .SingleAsync().ConfigureAwait(false));
        var audit = await dbContext.SecurityAuditEntries.AsNoTracking()
            .SingleAsync(entity => entity.ResourceId == fixture.CaseId && entity.Operation == "CaseCommand.ResolveCase")
            .ConfigureAwait(false);
        Assert.Equal("Allowed", audit.DecisionCode);
        Assert.Null(audit.ReasonCode);
        await CleanupCaseCommandRowsAsync(fixture).ConfigureAwait(false);
    }

    [CaseAuthorityPostgresFact]
    public async Task Approved_assignment_requires_prospective_target_scope_and_preserves_existing_follow_up_owner()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        await AddOperationsAsync(fixture,
        [
            CaseOperation.RequestProtectedAction,
            CaseOperation.ReviewProtectedAction,
            CaseOperation.AssignCase,
            CaseOperation.ManageFollowUp
        ]).ConfigureAwait(false);
        var targetGrantGroupId = await AddEligibleTargetGrantAsync(fixture).ConfigureAwait(false);
        cleanup.TrackTargetGrant(targetGrantGroupId);
        await SetInitialWorkflowVersionAsync(fixture).ConfigureAwait(false);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), provider).ConfigureAwait(false);

        var actor = fixture.CreateActor();
        var followUpId = Guid.NewGuid();
        var dueAt = new DateTimeOffset(2027, 1, 5, 9, 0, 0, TimeSpan.Zero);
        var schedule = new ScheduleFollowUpCommand(Guid.NewGuid(), 2, followUpId,
            "Synthetic follow-up", fixture.ActorId, dueAt, "Synthetic schedule before transfer", null, DateTimeOffset.UtcNow);
        var scheduled = await ProcessCommandAsync(fixture, actor, schedule, keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, scheduled.Status);
        Assert.Equal(3, scheduled.AggregateVersion);
        var requestId = Guid.NewGuid();
        var request = new RequestCaseActionCommand(
            Guid.NewGuid(), 3, requestId, CaseAction.Reassign, "synthetic assignment request", [],
            fixture.NewOwnerActorId, null, DateTimeOffset.UtcNow);
        var requestResult = await ProcessCommandAsync(fixture, actor, request, keyRing.CreateOptions(), provider)
            .ConfigureAwait(false);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, requestResult.Status);
        Assert.Equal(4, requestResult.AggregateVersion);

        var review = new ReviewCaseActionRequestCommand(
            Guid.NewGuid(), 4, requestId, CaseActionReviewOutcome.Approved,
            fixture.NewOwnerActorId, "synthetic approved assignment", null, DateTimeOffset.UtcNow);
        var reviewResult = await ProcessCommandAsync(fixture, actor, review, keyRing.CreateOptions(), provider)
            .ConfigureAwait(false);

        Assert.Equal(CaseCommandProcessingStatus.Accepted, reviewResult.Status);
        Assert.Equal(6, reviewResult.AggregateVersion);
        Assert.Equal(2, reviewResult.EventIds.Length);
        await using (var verifyContext = fixture.CreateDbContext())
        {
            var projection = await verifyContext.ManagedCases.AsNoTracking()
                .SingleAsync(entity => entity.CaseId == fixture.CaseId).ConfigureAwait(false);
            Assert.Equal(fixture.NewOwnerActorId, projection.AccountableOwnerActorId);
            Assert.Equal(6, projection.AggregateVersion);
            Assert.Equal(4, projection.ResourceVersion);

            var followUp = await verifyContext.ManagedCaseFollowUps.AsNoTracking()
                .SingleAsync(item => item.FollowUpId == followUpId);
            Assert.Equal(fixture.ActorId, followUp.AssigneeActorId);
            Assert.Equal(dueAt, followUp.DueAtUtc);
            Assert.Equal(FollowUpStatus.Scheduled, followUp.Status);
            Assert.Equal(3, followUp.ScheduledVersion);
            Assert.Null(followUp.OutcomeVersion);
            Assert.False(await verifyContext.CaseEvents.AnyAsync(item => item.CaseId == fixture.CaseId &&
                item.EventType == "CaseFollowUpOutcomeRecorded"));

            var ownerEpochs = await verifyContext.ActorAccessStates.AsNoTracking()
                .Where(entity => entity.ActorId == fixture.ActorId || entity.ActorId == fixture.NewOwnerActorId)
                .ToDictionaryAsync(entity => entity.ActorId, entity => entity.AccessEpoch).ConfigureAwait(false);
            Assert.Equal(fixture.AccessEpoch + 1, ownerEpochs[fixture.ActorId]);
            Assert.Equal(fixture.AccessEpoch + 1, ownerEpochs[fixture.NewOwnerActorId]);
        }

    }

    [CaseAuthorityPostgresFact]
    public async Task Final_expiry_denial_after_payload_protection_rolls_back_all_staged_effects()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        await AddOperationAsync(fixture, CaseOperation.ResolveCase).ConfigureAwait(false);
        await SetInitialWorkflowVersionAsync(fixture).ConfigureAwait(false);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), provider).ConfigureAwait(false);

        var expiryBaselineUtc = await ReadDatabaseUtcNowAsync(fixture, CancellationToken.None).ConfigureAwait(false);
        var grantExpiresAtUtc = expiryBaselineUtc.AddSeconds(12);
        await fixture.SetGrantExpiryAsync(grantExpiresAtUtc).ConfigureAwait(false);
        var commandId = Guid.NewGuid();
        var command = new ResolveCaseCommand(
            commandId,
            ExpectedVersion: 2,
            Reason: "rollback synthetic payload",
            OccurredAtUtc: null,
            RecordedAtUtc: DateTimeOffset.UtcNow);
        var stageTiming = new StageExpiryTiming(fixture, grantExpiresAtUtc);
        await using var dbContext = fixture.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        var eventCountBefore = await dbContext.CaseEvents.CountAsync(entity => entity.CaseId == fixture.CaseId)
            .ConfigureAwait(false);
        var stagedPayloadIds = new RecordingProtectedPayloadStore(
            new PostgresProtectedCaseEventPayloadStore(dbContext, unitOfWork, keyRing.CreateOptions(), provider),
            stageTiming.AfterStageAsync);
        var persistence = new PostgresCaseCommandPersistence(
            dbContext, unitOfWork, stagedPayloadIds, new CaseEventSerializer());
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(dbContext, unitOfWork),
            new PostgresCaseAccessDescriptorStore(dbContext, unitOfWork));
        var processor = new CaseCommandProcessor(unitOfWork, policy, persistence);

        var result = await processor.ProcessAsync(
            fixture.CreateActor(), fixture.CaseId, 1, command, CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(CaseCommandProcessingStatus.Denied, result.Status);
        Assert.True(stagedPayloadIds.StageCompleted);
        Assert.True(stageTiming.InitialAuthorizationWindowWasActive,
            "The initial authorization must still be inside the database-clock expiry window after payload staging.");
        Assert.True(stageTiming.FinalDatabaseTimeUtc >= grantExpiresAtUtc.AddSeconds(1),
            "The final database authorization check must occur after grant expiry with a one-second margin.");
        Assert.Single(stagedPayloadIds.PayloadIds);
        Assert.Equal(eventCountBefore, await dbContext.CaseEvents.CountAsync(entity => entity.CaseId == fixture.CaseId)
            .ConfigureAwait(false));
        Assert.False(await dbContext.ProtectedEventPayloads.AnyAsync(entity => stagedPayloadIds.PayloadIds.Contains(entity.PayloadId))
            .ConfigureAwait(false));
        Assert.False(await dbContext.CommandReceipts.AnyAsync(entity => entity.CaseId == fixture.CaseId && entity.CommandId == commandId)
            .ConfigureAwait(false));
        Assert.False(await dbContext.CaseEvents.AnyAsync(entity => entity.CaseId == fixture.CaseId && entity.CommandId == commandId)
            .ConfigureAwait(false));
        Assert.False(await dbContext.SecurityAuditEntries.AnyAsync(entity =>
                entity.ResourceId == fixture.CaseId && entity.Operation == "CaseCommand.ResolveCase")
            .ConfigureAwait(false));
        Assert.Empty(dbContext.ProtectedEventPayloads.Local);
        var projection = await dbContext.ManagedCases.AsNoTracking()
            .SingleAsync(entity => entity.CaseId == fixture.CaseId).ConfigureAwait(false);
        Assert.Equal(2, projection.AggregateVersion);
        Assert.Equal(1, projection.ReportRevision);
        Assert.Equal(1, projection.ResourceVersion);
        Assert.Equal(CaseWorkflow.Reported, projection.Workflow);
    }

    [InlineData("{\"reason\":\"first\",\"reason\":\"second\"}")]
    [InlineData("{\"reason\":\"reason\",\"unexpected\":\"extra\"}")]
    [InlineData("{}")]
    [CaseAuthorityPostgresTheory]
    public async Task Replay_rejects_duplicate_unknown_or_missing_envelope_payload_fields(string payloadJson)
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        await SetInitialWorkflowVersionAsync(fixture).ConfigureAwait(false);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), provider).ConfigureAwait(false);

        var actor = fixture.CreateActor();
        var commandId = Guid.NewGuid();
        var recordedAt = DateTimeOffset.UtcNow;
        var aggregate = await ReadAggregateAsync(fixture, actor, keyRing.CreateOptions(), provider).ConfigureAwait(false);
        var resolution = Assert.IsType<CaseResolved>(Assert.Single(aggregate.Decide(
            new ResolveCaseCommand(commandId, aggregate.AggregateVersion, "synthetic replay fixture", null, recordedAt),
            new ActorCapabilities(actor.ActorId, CaseCapability.ResolveCase))));
        var json = Encoding.UTF8.GetBytes($$"""
            {
              "schemaVersion": 1,
              "eventId": "{{resolution.EventId:D}}",
              "eventType": "CaseResolved",
              "caseId": "{{resolution.CaseId:D}}",
              "aggregateVersion": {{resolution.AggregateVersion}},
              "reportRevision": {{resolution.ReportRevision}},
              "commandId": "{{resolution.CommandId:D}}",
              "actorId": "{{resolution.ActorId}}",
              "occurredAtUtc": null,
              "recordedAtUtc": "{{resolution.RecordedAtUtc:O}}",
              "payload": {{payloadJson}}
            }
            """);

        await using (var writeContext = fixture.CreateDbContext())
        {
            var unitOfWork = new PostgresCaseCommandUnitOfWork(writeContext);
            var payloadStore = new PostgresProtectedCaseEventPayloadStore(
                writeContext, unitOfWork, keyRing.CreateOptions(), provider);
            await using var writeLease = await unitOfWork.BeginCaseAsync(
                actor, fixture.CaseId, [], CaseLeaseMode.Write, CancellationToken.None).ConfigureAwait(false);
            var payloadId = Guid.NewGuid();
            var payloadReference = await payloadStore.StageProtectedAsync(
                fixture.CaseId, payloadId, "CaseResolved", 1, json, CancellationToken.None).ConfigureAwait(false);
            writeContext.CommandReceipts.Add(new CommandReceiptEntity
            {
                ReceiptId = Guid.NewGuid(),
                ActorId = actor.ActorId,
                CommandId = commandId,
                CaseId = fixture.CaseId,
                CommandType = "ResolveCase",
                SchemaVersion = 1,
                CanonicalPayloadHash = SHA256.HashData([]),
                FirstAggregateVersion = resolution.AggregateVersion,
                LastAggregateVersion = resolution.AggregateVersion,
                ReportRevision = resolution.ReportRevision,
                AcceptedAtUtc = resolution.RecordedAtUtc
            });
            writeContext.CaseEvents.Add(new CaseEventEntity
            {
                EventId = resolution.EventId,
                CaseId = resolution.CaseId,
                AggregateVersion = resolution.AggregateVersion,
                ReportRevision = resolution.ReportRevision,
                CommandId = resolution.CommandId,
                ActorId = resolution.ActorId,
                EventType = "CaseResolved",
                SchemaVersion = 1,
                OccurredAtUtc = resolution.OccurredAtUtc,
                RecordedAtUtc = resolution.RecordedAtUtc,
                PayloadId = payloadReference.PayloadId
            });
            await writeLease.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await using var readContext = fixture.CreateDbContext();
        var readUnitOfWork = new PostgresCaseCommandUnitOfWork(readContext);
        var readPayloadStore = new PostgresProtectedCaseEventPayloadStore(
            readContext, readUnitOfWork, keyRing.CreateOptions(), provider);
        var persistence = new PostgresCaseCommandPersistence(
            readContext, readUnitOfWork, readPayloadStore, new CaseEventSerializer());
        await using var readLease = await readUnitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => persistence.ReplayCaseAsync(readLease, CancellationToken.None).AsTask()).ConfigureAwait(false);
        await readLease.DisposeAsync().ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(json);
    }

    private static async Task<CaseAggregate> ReadAggregateAsync(
        CaseAuthorityPostgresFixture fixture,
        ActorContext actor,
        ProtectedPayloadOptions options,
        ProtectedPayloadDataProtectionProvider provider)
    {
        await using var dbContext = fixture.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        var payloadStore = new PostgresProtectedCaseEventPayloadStore(dbContext, unitOfWork, options, provider);
        var persistence = new PostgresCaseCommandPersistence(
            dbContext, unitOfWork, payloadStore, new CaseEventSerializer());
        await using var lease = await unitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false);
        return await persistence.ReplayCaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task AddOperationAsync(CaseAuthorityPostgresFixture fixture, CaseOperation operation)
    {
        await using var dbContext = fixture.CreateDbContext();
        dbContext.AccessGrantOperations.Add(new AccessGrantOperationEntity
        {
            GrantGroupId = fixture.GrantGroupId,
            Operation = operation
        });
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task AddOperationsAsync(
        CaseAuthorityPostgresFixture fixture,
        IReadOnlyCollection<CaseOperation> operations)
    {
        await using var dbContext = fixture.CreateDbContext();
        dbContext.AccessGrantOperations.AddRange(operations.Select(operation => new AccessGrantOperationEntity
        {
            GrantGroupId = fixture.GrantGroupId,
            Operation = operation
        }));
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<string> AddEligibleTargetGrantAsync(CaseAuthorityPostgresFixture fixture)
    {
        var now = DateTimeOffset.UtcNow;
        var membershipId = Guid.NewGuid();
        var grantGroupId = $"test-target-grant-{Guid.NewGuid():N}";
        await using var dbContext = fixture.CreateDbContext();
        dbContext.OrganizationMemberships.Add(new OrganizationMembershipEntity
        {
            MembershipId = membershipId,
            ActorId = fixture.NewOwnerActorId,
            OrganizationId = fixture.OrganizationId,
            Status = PersistentMembershipStatus.Active,
            ValidFromUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddHours(2),
            GrantedByActorId = fixture.ActorId,
            Version = 1
        });
        dbContext.AccessGrantGroups.Add(new AccessGrantGroupEntity
        {
            GrantGroupId = grantGroupId,
            MembershipId = membershipId,
            ActorId = fixture.NewOwnerActorId,
            OrganizationId = fixture.OrganizationId,
            WorkspaceId = fixture.WorkspaceId,
            CountryCode = "SZ",
            Sector = Sector.HumanHealth,
            SensitivityCode = "sens-A",
            CanAccessJurisdictionalCases = false,
            Status = PersistentGrantStatus.Active,
            ValidFromUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddHours(2),
            GrantedByActorId = fixture.ActorId,
            Version = 1
        });
        dbContext.AccessGrantOperations.Add(new AccessGrantOperationEntity
        {
            GrantGroupId = grantGroupId,
            Operation = CaseOperation.ReadCase
        });
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        return grantGroupId;
    }

    private static async Task RemoveTargetGrantAsync(CaseAuthorityPostgresFixture fixture, string grantGroupId)
    {
        await using var dbContext = fixture.CreateDbContext();
        await dbContext.AccessGrantOperations.Where(entity => entity.GrantGroupId == grantGroupId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.AccessGrantRegions.Where(entity => entity.GrantGroupId == grantGroupId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.AccessGrantGroups.Where(entity => entity.GrantGroupId == grantGroupId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.OrganizationMemberships
            .Where(entity => entity.ActorId == fixture.NewOwnerActorId && entity.OrganizationId == fixture.OrganizationId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
    }

    private static async Task<CaseCommandProcessingResult> ProcessCommandAsync(
        CaseAuthorityPostgresFixture fixture,
        ActorContext actor,
        CaseCommand command,
        ProtectedPayloadOptions options,
        ProtectedPayloadDataProtectionProvider provider)
    {
        await using var dbContext = fixture.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        var payloadStore = new PostgresProtectedCaseEventPayloadStore(dbContext, unitOfWork, options, provider);
        var persistence = new PostgresCaseCommandPersistence(
            dbContext, unitOfWork, payloadStore, new CaseEventSerializer());
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(dbContext, unitOfWork),
            new PostgresCaseAccessDescriptorStore(dbContext, unitOfWork));
        var processor = new CaseCommandProcessor(unitOfWork, policy, persistence);
        return await processor.ProcessAsync(actor, fixture.CaseId, 1, command, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task SetInitialWorkflowVersionAsync(CaseAuthorityPostgresFixture fixture)
    {
        await using var dbContext = fixture.CreateDbContext();
        await dbContext.ManagedCases
            .Where(entity => entity.CaseId == fixture.CaseId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.WorkflowVersion, 1))
            .ConfigureAwait(false);
    }

    private static async Task<DateTimeOffset> ReadDatabaseUtcNowAsync(
        CaseAuthorityPostgresFixture fixture,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT clock_timestamp() AT TIME ZONE 'UTC'", connection);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DateTime timestamp
            ? new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc))
            : throw new InvalidOperationException("The database UTC clock could not be read for the expiry test.");
    }

    private static async Task CleanupCaseCommandRowsAsync(CaseAuthorityPostgresFixture fixture)
    {
        await using var dbContext = fixture.CreateDbContext();
        var payloadIds = await dbContext.CaseEvents.AsNoTracking()
            .Where(entity => entity.CaseId == fixture.CaseId)
            .Select(entity => entity.PayloadId)
            .ToArrayAsync().ConfigureAwait(false);
        var eventIds = await dbContext.CaseEvents.AsNoTracking()
            .Where(entity => entity.CaseId == fixture.CaseId)
            .Select(entity => entity.EventId)
            .ToArrayAsync().ConfigureAwait(false);
        var notificationIds = await dbContext.NotificationItems.AsNoTracking()
            .Where(entity => entity.ResourceId == fixture.CaseId)
            .Select(entity => entity.NotificationId)
            .ToArrayAsync().ConfigureAwait(false);
        await dbContext.NotificationDeliveryOutbox.Where(entity => notificationIds.Contains(entity.NotificationId))
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.NotificationItems.Where(entity => entity.ResourceId == fixture.CaseId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.DomainOutbox.Where(entity => eventIds.Contains(entity.EventId))
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.ManagedCaseFollowUps.Where(entity => entity.CaseId == fixture.CaseId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.CaseEvents.Where(entity => entity.CaseId == fixture.CaseId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.CommandReceipts.Where(entity => entity.CaseId == fixture.CaseId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.ProtectedEventPayloads.Where(entity => payloadIds.Contains(entity.PayloadId))
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.SecurityAuditEntries.Where(entity => entity.ResourceId == fixture.CaseId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
    }

    private sealed class CaseCommandProcessingCleanup(CaseAuthorityPostgresFixture fixture) : IAsyncDisposable
    {
        private string? _targetGrantGroupId;

        public void TrackTargetGrant(string grantGroupId) => _targetGrantGroupId = grantGroupId;

        public async ValueTask DisposeAsync()
        {
            if (_targetGrantGroupId is not null)
            {
                await RemoveTargetGrantAsync(fixture, _targetGrantGroupId).ConfigureAwait(false);
            }

            await CleanupCaseCommandRowsAsync(fixture).ConfigureAwait(false);
        }
    }

    private static async Task SeedInitialJournalAsync(
        CaseAuthorityPostgresFixture fixture,
        ProtectedPayloadOptions options,
        ProtectedPayloadDataProtectionProvider provider)
    {
        var actor = fixture.CreateActor();
        await using var dbContext = fixture.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        var store = new PostgresProtectedCaseEventPayloadStore(dbContext, unitOfWork, options, provider);
        var serializer = new CaseEventSerializer();
        await using var lease = await unitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Write, CancellationToken.None).ConfigureAwait(false);
        var commandId = Guid.NewGuid();
        var recordedAt = DateTimeOffset.UtcNow;
        var report = new ReportFacts(
            CaseKind.Human,
            Sector.HumanHealth,
            "synthetic protected report text",
            Array.AsReadOnly<ReportedObservation>([new ReportedObservation("synthetic observation")]));
        var initialOwner = await dbContext.ManagedCases.Where(item => item.CaseId == fixture.CaseId)
            .Select(item => item.AccountableOwnerActorId).SingleAsync();
        var initialEvents = CaseAggregate.Create(
            new AcceptCaseCommand(
                commandId, fixture.CaseId, report, initialOwner, fixture.WorkspaceId, null, recordedAt),
            new ActorCapabilities(initialOwner, CaseCapability.AcceptReport | CaseCapability.AssignCase));

        dbContext.CommandReceipts.Add(new CommandReceiptEntity
        {
            ReceiptId = Guid.NewGuid(),
            ActorId = initialOwner,
            CommandId = commandId,
            CaseId = fixture.CaseId,
            CommandType = "AcceptCase",
            SchemaVersion = 1,
            CanonicalPayloadHash = SHA256.HashData([]),
            FirstAggregateVersion = 1,
            LastAggregateVersion = 2,
            ReportRevision = 1,
            AcceptedAtUtc = recordedAt
        });

        foreach (var @event in initialEvents)
        {
            var eventType = @event.GetType().Name;
            var serialized = serializer.SerializeToUtf8Bytes(@event);
            try
            {
                var payloadId = Guid.NewGuid();
                var reference = await store.StageProtectedAsync(
                    fixture.CaseId, payloadId, eventType, 1, serialized, CancellationToken.None).ConfigureAwait(false);
                dbContext.CaseEvents.Add(new CaseEventEntity
                {
                    EventId = @event.EventId,
                    CaseId = @event.CaseId,
                    AggregateVersion = @event.AggregateVersion,
                    ReportRevision = @event.ReportRevision,
                    CommandId = @event.CommandId,
                    ActorId = @event.ActorId,
                    EventType = eventType,
                    SchemaVersion = 1,
                    OccurredAtUtc = @event.OccurredAtUtc,
                    RecordedAtUtc = @event.RecordedAtUtc,
                    PayloadId = reference.PayloadId
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serialized);
            }
        }

        await lease.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class StageExpiryTiming(
        CaseAuthorityPostgresFixture fixture,
        DateTimeOffset grantExpiresAtUtc)
    {
        public bool InitialAuthorizationWindowWasActive { get; private set; }

        public DateTimeOffset FinalDatabaseTimeUtc { get; private set; }

        public async ValueTask AfterStageAsync(CancellationToken cancellationToken)
        {
            var stagedAtUtc = await ReadDatabaseUtcNowAsync(fixture, cancellationToken).ConfigureAwait(false);
            var applicationClockAtStageUtc = DateTimeOffset.UtcNow;
            InitialAuthorizationWindowWasActive = stagedAtUtc <= grantExpiresAtUtc.AddSeconds(-2) &&
                applicationClockAtStageUtc <= grantExpiresAtUtc.AddSeconds(-2);
            if (!InitialAuthorizationWindowWasActive)
            {
                throw new InvalidOperationException(
                    "Payload staging did not complete with the initial authorization window safely active.");
            }

            var expiryObservationTargetUtc = grantExpiresAtUtc.AddSeconds(1);
            var delay = expiryObservationTargetUtc - stagedAtUtc;
            if (delay < TimeSpan.Zero || delay > TimeSpan.FromSeconds(20))
            {
                throw new InvalidOperationException("The database expiry observation was outside its bounded delay window.");
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            FinalDatabaseTimeUtc = await ReadDatabaseUtcNowAsync(fixture, cancellationToken).ConfigureAwait(false);
            if (FinalDatabaseTimeUtc < expiryObservationTargetUtc)
            {
                throw new InvalidOperationException(
                    "The database clock did not reach the post-expiry margin before the final authorization check.");
            }
        }
    }

    private sealed class RecordingProtectedPayloadStore(
        IProtectedCaseEventPayloadStore inner,
        Func<CancellationToken, ValueTask>? afterStage = null) : IProtectedCaseEventPayloadStore
    {
        public List<Guid> PayloadIds { get; } = [];

        public bool StageCompleted { get; private set; }

        public async ValueTask<ProtectedCaseEventPayloadReference> StageProtectedAsync(
            Guid caseId,
            Guid payloadId,
            string eventType,
            int schemaVersion,
            ReadOnlyMemory<byte> serializedBytes,
            CancellationToken cancellationToken)
        {
            PayloadIds.Add(payloadId);
            var result = await inner.StageProtectedAsync(caseId, payloadId, eventType, schemaVersion, serializedBytes, cancellationToken)
                .ConfigureAwait(false);
            StageCompleted = true;
            if (afterStage is not null)
            {
                await afterStage(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        public ValueTask<byte[]> UnprotectAsync(
            Guid caseId,
            Guid payloadId,
            string eventType,
            int schemaVersion,
            string protectionPurposeVersion,
            ReadOnlyMemory<byte> protectedBytes,
            CancellationToken cancellationToken) =>
            inner.UnprotectAsync(caseId, payloadId, eventType, schemaVersion, protectionPurposeVersion, protectedBytes, cancellationToken);
    }

    private sealed class SyntheticPayloadKeyRing : IDisposable
    {
        private readonly string _rootPath;
        private readonly string _certificatePath;
        private readonly string _certificatePassword;

        private SyntheticPayloadKeyRing(string rootPath, string keysPath, string certificatePath, string certificatePassword)
        {
            _rootPath = rootPath;
            KeysPath = keysPath;
            _certificatePath = certificatePath;
            _certificatePassword = certificatePassword;
        }

        public string KeysPath { get; }

        public static SyntheticPayloadKeyRing Create()
        {
            var root = PrivateKeyRingTestDirectory.CreateRoot("swiftaid-command-processing");
            var keys = Path.Combine(root, "keys");
            var certificatePath = Path.Combine(root, "protection.pfx");
            try
            {
                keys = PrivateKeyRingTestDirectory.CreateKeyDirectory(root);

                var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=SwiftAid synthetic command processing", rsa,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
                var bytes = certificate.Export(X509ContentType.Pfx, password);
                try
                {
                    File.WriteAllBytes(certificatePath, bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }

                return new SyntheticPayloadKeyRing(root, keys, certificatePath, password);
            }
            catch
            {
                PrivateKeyRingTestDirectory.DeleteRoot(root);
                throw;
            }
        }

        public ProtectedPayloadOptions CreateOptions() => new()
        {
            Enabled = true,
            ApplicationName = "SwiftAid.SyntheticCommandProcessingTests",
            KeysPath = KeysPath,
            CertificatePath = _certificatePath,
            CertificatePassword = _certificatePassword,
            MaximumPayloadBytes = 1024 * 1024
        };

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "SwiftAid.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
