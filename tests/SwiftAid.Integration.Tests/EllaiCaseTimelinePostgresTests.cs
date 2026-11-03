using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Commands;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

public sealed partial class CaseCommandProcessingPostgresTests
{
    [FrameworkPostgresFact]
    public async Task EllaiTimelineUsesRealCaseAuthorizationAndDeniesRevokedRead()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        await SetInitialWorkflowVersionAsync(fixture);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var protection = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), protection);
        var consent = new EllaiConsentService();
        consent.Grant("timeline-fixture", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromMinutes(5));
        var toolContext = new EllaiToolContext("timeline-fixture", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: fixture.CreateActor(),
            AllowedTools: new HashSet<string> { EllaiToolNames.CaseTimeline });
        var request = new EllaiToolRequest(EllaiToolNames.CaseTimeline,
            new Dictionary<string, string> { ["case_id"] = fixture.CaseId.ToString("D") });

        async Task<EllaiToolResult> ReadAsync()
        {
            await using var db = fixture.CreateDbContext();
            var unit = new PostgresCaseCommandUnitOfWork(db);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit),
                new PostgresCaseAccessDescriptorStore(db, unit));
            var query = new CaseTimelineQuery(new PostgresCaseTimelineStore(db), policy, unit);
            return await new EllaiToolBroker(new EllaiPolicyService(), consent, timeline: query)
                .InvokeAsync(toolContext, request, default);
        }

        var allowed = await ReadAsync();
        Assert.True(allowed.Allowed);
        Assert.NotEmpty(allowed.ResultSummary);
        Assert.Equal(fixture.CaseId.ToString("D"), Assert.Single(allowed.Evidence).ResourceId);
        await using (var db = fixture.CreateDbContext())
            await db.AccessGrantOperations.Where(item => item.GrantGroupId == fixture.GrantGroupId && item.Operation == CaseOperation.ReadCase)
                .ExecuteDeleteAsync();
        var denied = await ReadAsync();
        Assert.False(denied.Allowed);
        Assert.Empty(denied.Evidence);
    }
}
