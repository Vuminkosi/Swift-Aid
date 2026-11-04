using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class InvestigationPostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task Investigation_uses_durable_turns_fences_pause_and_rechecks_scope()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateDbContext());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<PostgresSessionScopeService>();
        services.AddScoped<IScopeService, GatewaySessionScopeService>();
        services.AddScoped<EllaiSessionScopeResolver>();
        services.AddScoped<IEllaiSessionStore, PostgresEllaiSessionStore>();
        services.AddScoped<EllaiSessionService>();
        services.AddScoped<EllaiUtteranceSubmissionService>();
        services.AddSingleton<EllaiConsentService>();
        services.AddSingleton<IEllaiApprovalStore, EllaiReceiptStore>();
        await using var provider = services.BuildServiceProvider();
        var auth = new Authentication(fixture.ActorId, fixture.AccessEpoch);
        var protection = new EphemeralDataProtectionProvider();
        InvestigationGatewayClient Client() => new(auth, provider.GetRequiredService<IServiceScopeFactory>(), protection, NullLogger<InvestigationGatewayClient>.Instance);
        try
        {
            var started = await Client().StartAsync(new("Synthetic delivery review", "Check whether the recorded delivery was acknowledged."));
            Assert.Equal(UiCapabilityResultCode.Success, started.Code);
            var initial = started.Value!;
            Assert.False(initial.IsSynthetic);
            Assert.Equal(InvestigationStatus.GatheringEvidence, initial.Status);
            var stored = await db.Set<AgentInvestigationEntity>().SingleAsync(row => row.SessionId == initial.Id);
            Assert.DoesNotContain("Synthetic delivery", stored.ProtectedRequest);
            Assert.Single((await Client().ListAsync(new())).Value!.Items);
            var paused = await Client().PauseAsync(initial.Id, initial.Version);
            Assert.Equal(InvestigationStatus.Paused, paused.Value!.Status);
            Assert.Equal(UiCapabilityResultCode.Conflict, (await Client().ResumeAsync(initial.Id, initial.Version)).Code);
            var resumed = await Client().ResumeAsync(initial.Id, paused.Value.Version);
            Assert.Equal(UiCapabilityResultCode.Success, resumed.Code);
            Assert.True(resumed.Value!.Version > paused.Value.Version);
            var decision = new EllaiDecision(EllaiDecision.CurrentSchemaVersion, DecisionKind.NeedInput, "Who received the delivery?", "Who received the delivery?",
                [], [new EllaiQuestion("recipient", "Who received the delivery?", [])], [], null, [], null, null);
            var latest = await db.EllaiUtterances.Where(row => row.SessionId == initial.Id).OrderByDescending(row => row.Sequence).FirstAsync();
            latest.Outcome = "Completed"; latest.AssistantText = decision.DisplayText; latest.DecisionJson = JsonSerializer.Serialize(decision);
            latest.CompletedAtUtc = DateTimeOffset.UtcNow; latest.WorkPayloadJson = null; latest.LeaseExpiresAtUtc = null;
            await db.SaveChangesAsync();
            var question = await Client().GetAsync(initial.Id);
            Assert.Equal(InvestigationStatus.NeedsInput, question.Value!.Status);
            Assert.Equal("recipient", Assert.Single(question.Value.Clarifications).Id);
            Assert.Equal(UiCapabilityResultCode.InvalidRequest, (await Client().ReplyAsync(initial.Id, question.Value.Version, "not-current", "Site coordinator")).Code);
            Assert.Equal(UiCapabilityResultCode.Success, (await Client().ReplyAsync(initial.Id, question.Value.Version, "recipient", "Site coordinator")).Code);
            await db.AccessGrantGroups.Where(row => row.GrantGroupId == fixture.GrantGroupId)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, PersistentGrantStatus.Revoked));
            Assert.Equal(UiCapabilityResultCode.Denied, (await Client().GetAsync(initial.Id)).Code);
        }
        finally
        {
            var ids = await db.Set<AgentInvestigationEntity>().Where(row => row.OwnerActorId == fixture.ActorId).Select(row => row.SessionId).ToArrayAsync();
            await db.Set<AgentInvestigationEntity>().Where(row => ids.Contains(row.SessionId)).ExecuteDeleteAsync();
            await db.EllaiUtterances.Where(row => ids.Contains(row.SessionId)).ExecuteDeleteAsync();
            await db.EllaiCheckpoints.Where(row => ids.Contains(row.SessionId)).ExecuteDeleteAsync();
            await db.EllaiSessions.Where(row => ids.Contains(row.SessionId)).ExecuteDeleteAsync();
        }
    }
    private sealed class Authentication(string actor, long epoch) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("sub", actor), new Claim(ActorContext.AccessEpochClaimType, epoch.ToString())], "fixture"))));
    }
}