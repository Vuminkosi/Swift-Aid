using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Services;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;
using SwiftAid.Gateway.Cases;
using SwiftAid.UI.Components.Surfaces.Operations.CaseDetail;

namespace SwiftAid.Integration.Tests;

public sealed partial class CaseCommandProcessingPostgresTests
{
    [OfficerReviewBrowserFact]
    public async Task OfficerReviewBrowserPersistsOneAssignmentThroughRealGatewayAndPostgres()
    {
        var fixture = await CaseAuthorityPostgresFixture.CreateAsync();
        await AddOperationsAsync(fixture, [CaseOperation.ManageEscalation, CaseOperation.ManageFollowUp]);
        var targetGrant = await AddEligibleTargetGrantAsync(fixture);
        await SetInitialWorkflowVersionAsync(fixture);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), provider);
        var raised = await ProcessCommandAsync(fixture, fixture.CreateActor(),
            new RaiseEscalationCommand(Guid.NewGuid(), 2, Guid.NewGuid(), "synthetic review", [], "synthetic-route", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, raised.Status);
        await using var db = fixture.CreateDbContext();
        await db.AccessGrantGroups.Where(group => group.GrantGroupId == targetGrant)
            .ExecuteUpdateAsync(update => update.SetProperty(group => group.CanAccessJurisdictionalCases, true));
        db.AccessGrantRegions.Add(new AccessGrantRegionEntity { GrantGroupId = targetGrant, RegionId = fixture.CaseRegionId });
        db.AccessGrantOperations.AddRange(
            new AccessGrantOperationEntity { GrantGroupId = targetGrant, Operation = CaseOperation.ManageEscalation },
            new AccessGrantOperationEntity { GrantGroupId = targetGrant, Operation = CaseOperation.ManageFollowUp });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO health_officer_designations (designation_id, actor_id, organization_id, country_code, sector, status,
                valid_from_utc, expires_at_utc, designated_by_actor_id, version, created_at_utc, updated_at_utc)
            VALUES ({Guid.NewGuid()}, {fixture.NewOwnerActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Active',
                clock_timestamp() - interval '1 day', clock_timestamp() + interval '1 day', {fixture.ActorId}, 1, clock_timestamp(), clock_timestamp())
            """);
        var root = FindOfficerBrowserRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development", ContentRootPath = Path.Combine(root, "src", "SwiftAid.Gateway"),
            ApplicationName = typeof(OfficerReviewBrowserApp).Assembly.GetName().Name
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.UseSetting(WebHostDefaults.StaticWebAssetsKey,
            Path.Combine(root, "src", "SwiftAid.Gateway", "bin", "Debug", "net10.0", "SwiftAid.Gateway.staticwebassets.runtime.json"));
        builder.WebHost.UseStaticWebAssets();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddMudServices();
        builder.Services.AddSingleton(new OfficerReviewBrowserCase(fixture.CaseId));
        builder.Services.AddScoped<AuthenticationStateProvider>(_ => new FixtureOfficerAuth(Principal(fixture.ActorId, fixture.AccessEpoch)));
        builder.Services.AddScoped<IOfficerEscalationReviewClient, OfficerEscalationReviewGatewayClient>();
        var settings = new HealthOfficerDirectoryOptions { Enabled = true, EscalationApprovalsEnabled = true, EscalationAssignmentsEnabled = true };
        builder.Services.AddScoped(_ => fixture.CreateDbContext());
        builder.Services.AddScoped<IHealthOfficerEscalationReviewQuery>(services =>
        {
            var scopedDb = services.GetRequiredService<SwiftAidDbContext>();
            var unit = new PostgresCaseCommandUnitOfWork(scopedDb);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(scopedDb, unit), new PostgresCaseAccessDescriptorStore(scopedDb, unit));
            return new HealthOfficerEscalationReviewQuery(unit, policy, new PostgresHealthOfficerEscalationReviewStore(scopedDb, unit),
                new PostgresEligibleHealthOfficerStore(scopedDb, unit), settings);
        });
        builder.Services.AddScoped<IHealthOfficerEscalationApprovalService>(services =>
        {
            var scopedDb = services.GetRequiredService<SwiftAidDbContext>();
            var unit = new PostgresCaseCommandUnitOfWork(scopedDb);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(scopedDb, unit), new PostgresCaseAccessDescriptorStore(scopedDb, unit));
            return new PostgresHealthOfficerEscalationApprovalService(scopedDb, unit, policy, new PostgresEligibleHealthOfficerStore(scopedDb, unit), settings);
        });
        builder.Services.AddScoped<IHealthOfficerEscalationAssignmentService>(services =>
        {
            var scopedDb = services.GetRequiredService<SwiftAidDbContext>();
            var unit = new PostgresCaseCommandUnitOfWork(scopedDb);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(scopedDb, unit), new PostgresCaseAccessDescriptorStore(scopedDb, unit));
            var payload = new PostgresProtectedCaseEventPayloadStore(scopedDb, unit, keyRing.CreateOptions(), provider);
            return new PostgresHealthOfficerEscalationAssignmentService(scopedDb, unit, policy,
                new PostgresCaseCommandPersistence(scopedDb, unit, payload, new CaseEventSerializer()),
                new PostgresEligibleHealthOfficerStore(scopedDb, unit), settings);
        });
        await using var app = builder.Build();
        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapRazorComponents<OfficerReviewBrowserApp>().AddInteractiveServerRenderMode();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var start = new ProcessStartInfo("node") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "tests", "SwiftAid.Ui.Tests", "officer-escalation-postgres.browser.cjs"));
        start.Environment["SWIFTAID_OFFICER_BROWSER_URL"] = address;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        Assert.Equal(1, await db.ManagedCaseFollowUps.CountAsync(item => item.CaseId == fixture.CaseId));
        Assert.Equal(4, await db.CaseEvents.CountAsync(item => item.CaseId == fixture.CaseId));
        var assignmentCount = await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM health_officer_escalation_assignments WHERE case_id = {fixture.CaseId}").SingleAsync();
        Assert.Equal(1, assignmentCount);
    }

    private static string FindOfficerBrowserRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "spec.md"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

public sealed class OfficerReviewBrowserFactAttribute : FactAttribute
{
    public OfficerReviewBrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SWIFTAID_RUN_OFFICER_BROWSER") != "1" || Environment.GetEnvironmentVariable("SWIFTAID_RUN_SQLAUTH_FIXTURES") != "1")
            Skip = "Requires opt-in disposable PostgreSQL and installed Playwright/Chromium.";
    }
}

public sealed record OfficerReviewBrowserCase(Guid CaseId);

public sealed class OfficerReviewBrowserApp : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.AddMarkupContent(0, "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><base href=\"/\"><link rel=\"stylesheet\" href=\"/_content/MudBlazor/MudBlazor.min.css\"></head><body>");
        builder.OpenComponent<OfficerReviewBrowserSurface>(1);
        builder.AddComponentRenderMode(new InteractiveServerRenderMode(prerender: false));
        builder.CloseComponent();
        builder.AddMarkupContent(2, "<script src=\"/_content/MudBlazor/MudBlazor.min.js\"></script><script src=\"/_framework/blazor.web.js\"></script></body></html>");
    }
}

[Route("/officer-review")]
public sealed class OfficerReviewBrowserSurface : ComponentBase
{
    [Inject] public OfficerReviewBrowserCase Case { get; set; } = default!;
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<MudThemeProvider>(0); builder.CloseComponent();
        builder.OpenComponent<MudPopoverProvider>(1); builder.CloseComponent();
        builder.OpenComponent<OfficerEscalationReviewPanel>(2);
        builder.AddAttribute(3, nameof(OfficerEscalationReviewPanel.CaseId), Case.CaseId);
        builder.CloseComponent();
    }
}
