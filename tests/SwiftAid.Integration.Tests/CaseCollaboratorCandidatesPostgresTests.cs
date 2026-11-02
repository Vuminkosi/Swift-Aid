using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

public sealed partial class CaseCommandProcessingPostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task Collaborator_candidates_require_management_and_keep_permission_groups_separate_across_pages()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync();
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        var targetKey = await AddEligibleTargetGrantAsync(fixture);
        cleanup.TrackTargetGrant(targetKey);

        async Task<bool> QueryIsAllowed()
        {
            await using var db = fixture.CreateDbContext();
            var unit = new PostgresCaseCommandUnitOfWork(db);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
            var query = new CaseCollaboratorCandidatesQuery(new PostgresCaseCollaboratorCandidatesStore(db), policy, unit);
            var result = await query.GetAsync(fixture.CreateActor(), fixture.CaseId, null, null, 25, CancellationToken.None);
            if (result is not null) Assert.Equal(fixture.NewOwnerActorId, Assert.Single(result.Items).ActorId);
            return result is not null;
        }

        Assert.False(await QueryIsAllowed());
        await AddOperationAsync(fixture, CaseOperation.ManageCollaborators);
        Assert.True(await QueryIsAllowed());
        await using var context = fixture.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(context);
        await using var lease = await unitOfWork.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId,
            [fixture.NewOwnerActorId], CaseLeaseMode.Write, CancellationToken.None);
        var source = await context.AccessGrantGroups.SingleAsync(item => item.GrantGroupId == targetKey);
        var second = (AccessGrantGroupEntity)context.Entry(source).CurrentValues.ToObject();
        second.GrantGroupId = targetKey + "-second";
        context.AccessGrantGroups.Add(second);
        context.AccessGrantOperations.AddRange(
            new AccessGrantOperationEntity { GrantGroupId = second.GrantGroupId, Operation = CaseOperation.ReadCase },
            new AccessGrantOperationEntity { GrantGroupId = second.GrantGroupId, Operation = CaseOperation.ManageFollowUp },
            new AccessGrantOperationEntity { GrantGroupId = second.GrantGroupId, Operation = CaseOperation.ManageCollaborators });
        await context.SaveChangesAsync();
        var store = new PostgresCaseCollaboratorCandidatesStore(context);
        var firstPage = await store.GetAsync(fixture.CaseId, "", null, 1, CancellationToken.None);
        var first = Assert.Single(firstPage.Items);
        Assert.Equal(targetKey, first.Key);
        Assert.Equal(["ReadCase"], first.Operations.ToArray());
        Assert.Null(first.SafeDisplayLabel);
        Assert.Equal(first.Key, firstPage.NextCursor);
        var secondPage = await store.GetAsync(fixture.CaseId, "", firstPage.NextCursor, 1, CancellationToken.None);
        var next = Assert.Single(secondPage.Items);
        Assert.Equal(second.GrantGroupId, next.Key);
        Assert.Equal(["ReadCase", "ManageFollowUp"], next.Operations.ToArray());
        Assert.Null(secondPage.NextCursor);
        Assert.Empty((await store.GetAsync(fixture.CaseId, "absent-name", null, 25, CancellationToken.None)).Items);

        source.CountryCode = "ZA";
        second.SensitivityCode = "different-scope";
        await context.SaveChangesAsync();
        Assert.Empty((await store.GetAsync(fixture.CaseId, "", null, 25, CancellationToken.None)).Items);
        source.CountryCode = "SZ";
        second.SensitivityCode = "sens-A";
        source.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        second.Status = PersistentGrantStatus.Revoked;
        await context.SaveChangesAsync();
        Assert.Empty((await store.GetAsync(fixture.CaseId, "", null, 25, CancellationToken.None)).Items);
        source.ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1);
        await context.SaveChangesAsync();
        Assert.Single((await store.GetAsync(fixture.CaseId, "", null, 25, CancellationToken.None)).Items);
        context.CaseShares.Add(new CaseShareEntity
        {
            CaseId = fixture.CaseId, ActorId = fixture.NewOwnerActorId, GrantedByActorId = fixture.ActorId,
            Version = 1, IsActive = true, GrantedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();
        Assert.Empty((await store.GetAsync(fixture.CaseId, "", null, 25, CancellationToken.None)).Items);
        // All altered/extra grants and the share are rolled back before fixture cleanup.
    }
}
