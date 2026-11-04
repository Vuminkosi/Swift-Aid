using System.Diagnostics;
using System.Security.Claims;
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
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Services;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Intake;
using SwiftAid.Gateway.Components.Pages;
using SwiftAid.Gateway.Ingestion;
using SwiftAid.Gateway.Presentation;
using SwiftAid.Infrastructure.Persistence.Repositories.Intake;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.IntakeImports;
using Xunit.Abstractions;

namespace SwiftAid.Integration.Tests;

/// <summary>Opt-in browser fixture: real component, configured policy, registration and PostgreSQL; synthetic identity and metadata.</summary>
[Collection(IntakePostgresCollection.Name)]
public sealed class CaseImportBrowserTests(ITestOutputHelper output)
{
    [ImportBrowserFact]
    public async Task Workspace_file_pause_refresh_resume_and_saved_cases_work_in_chromium()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var shared = await SharedWorkspaceImportTests.AddOrganizationAsync(fixture.Db, CaseImportPostgresTests.Actor.ActorId);
        var root = FindRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development", ContentRootPath = Path.Combine(root, "src", "SwiftAid.Gateway"),
            ApplicationName = typeof(ImportBrowserApp).Assembly.GetName().Name
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.UseSetting(WebHostDefaults.StaticWebAssetsKey,
            Path.Combine(root, "src", "SwiftAid.Gateway", "bin", "Debug", "net10.0", "SwiftAid.Gateway.staticwebassets.runtime.json"));
        builder.WebHost.UseStaticWebAssets();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddMudServices();
        builder.Services.AddScoped<AuthenticationStateProvider, FixtureAuthentication>();
        builder.Services.AddScoped<IWorkspaceImportClient, WorkspaceImportClient>();
        builder.Services.AddSingleton<IPublicIntakeMetadataClient, FixtureMetadata>();
        builder.Services.Configure<IntakeConsentOptions>(builder.Configuration.GetSection(IntakeConsentOptions.SectionName));
        builder.Services.AddSingleton<IPublicIntakeProcessingPolicyClient, GatewayPublicIntakeProcessingPolicyClient>();
        builder.Services.AddScoped(_ => new SwiftAidDbContext(new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql(fixture.Db.Database.GetConnectionString()).UseOpenIddict().Options));
        builder.Services.AddScoped(services => new PostgresCaseImportStore(services.GetRequiredService<SwiftAidDbContext>(), fixture.Protection, TimeProvider.System));
        builder.Services.AddScoped<ICaseImportStore>(services => services.GetRequiredService<PostgresCaseImportStore>());
        builder.Services.AddScoped<ICaseImportWorkspaceReader>(services => services.GetRequiredService<PostgresCaseImportStore>());
        builder.Services.AddScoped(services => fixture.CreateService(services.GetRequiredService<SwiftAidDbContext>(), services.GetRequiredService<ICaseImportStore>()));
        await using var app = builder.Build();
        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapRazorComponents<ImportBrowserApp>().AddInteractiveServerRenderMode();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var commands = Enumerable.Range(0, 250).Select(_ => Guid.NewGuid()).ToArray();
        var csv = CaseImportPostgresTests.Csv(commands[0]);
        csv += string.Concat(commands.Skip(1).Select(command => CaseImportPostgresTests.Csv(command).Split('\n', 2)[1]));
        var directory = Path.Combine(root, ".codex", "case-list-browser", "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "synthetic-import.csv");
        await File.WriteAllTextAsync(file, csv);
        var sharedCommand = Guid.NewGuid();
        var sharedFile = Path.Combine(directory, "synthetic-shared-import.csv");
        await File.WriteAllTextAsync(sharedFile, CaseImportPostgresTests.Csv(sharedCommand));
        var start = new ProcessStartInfo("node") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "tests", "SwiftAid.Ui.Tests", "workspace-import.browser.cjs"));
        start.Environment["SWIFTAID_IMPORT_BROWSER_URL"] = address;
        start.Environment["SWIFTAID_IMPORT_BROWSER_FILE"] = file;
        start.Environment["SWIFTAID_IMPORT_BROWSER_ARTIFACTS"] = directory;
        start.Environment["SWIFTAID_IMPORT_BROWSER_SHARED_ORG"] = shared.Organization;
        start.Environment["SWIFTAID_IMPORT_BROWSER_SHARED_FILE"] = sharedFile;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        output.WriteLine(await stdout);
        output.WriteLine(await stderr);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(250, await fixture.Db.CommandReceipts.CountAsync(row => commands.Contains(row.CommandId)));
        Assert.Equal(500, await fixture.Db.CaseEvents.CountAsync(row => commands.Contains(row.CommandId)));
        var sharedCase = await fixture.Db.CommandReceipts.Where(row => row.CommandId == sharedCommand).Select(row => row.CaseId).SingleAsync();
        Assert.True(await fixture.Db.ManagedCases.AnyAsync(row => row.CaseId == sharedCase && row.OrganizationId == shared.Organization && row.WorkspaceId == "workspace-1"));
        File.Delete(file);
        File.Delete(sharedFile);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "spec.md"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "officer-1"), new Claim(ActorContext.AccessEpochClaimType, "7")], "synthetic-fixture"))));
    }

    private sealed class FixtureMetadata : IPublicIntakeMetadataClient
    {
        private static UiCapabilityResult<T> Result<T>(T value) => new(UiCapabilityResultCode.Success, value,
            new("import-test-fixture", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true));
        public Task<UiCapabilityResult<PublicIntakeMetadata>> GetMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result(new PublicIntakeMetadata("1", "catalogue/fixture", "fixture-countries", "1", "fixture-privacy")));
        public Task<UiCapabilityResult<PublicIntakeCountryCatalogue>> GetCountriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result(new PublicIntakeCountryCatalogue("catalogue/fixture", "fixture-countries", [new("ZW", "Zimbabwe")])));
    }
}

public sealed class ImportBrowserFactAttribute : FactAttribute
{
    public ImportBrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SWIFTAID_RUN_IMPORT_BROWSER") != "1" ||
            Environment.GetEnvironmentVariable("SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES") != "1")
            Skip = "Requires opt-in disposable PostgreSQL and installed Playwright/Chromium.";
    }
}

public sealed class ImportBrowserApp : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.AddMarkupContent(0, "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><base href=\"/\"><link rel=\"stylesheet\" href=\"/_content/MudBlazor/MudBlazor.min.css\"><link rel=\"stylesheet\" href=\"/_content/SwiftAid.UI/css/swiftaid-theme.css\"><link rel=\"stylesheet\" href=\"/SwiftAid.Gateway.styles.css\"></head><body>");
        builder.OpenComponent<ImportBrowserSurface>(1);
        builder.AddComponentRenderMode(new InteractiveServerRenderMode(prerender: false));
        builder.CloseComponent();
        builder.AddMarkupContent(2, "<script src=\"/_content/MudBlazor/MudBlazor.min.js\"></script><script src=\"/_framework/blazor.web.js\"></script></body></html>");
    }
}

[Route("/workspace/imports")]
public sealed class ImportBrowserSurface : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<MudThemeProvider>(0); builder.CloseComponent();
        builder.OpenComponent<MudPopoverProvider>(1); builder.CloseComponent();
        builder.OpenComponent<MudDialogProvider>(2); builder.CloseComponent();
        builder.OpenComponent<MudSnackbarProvider>(3); builder.CloseComponent();
        builder.OpenComponent<CaseImports>(4); builder.CloseComponent();
    }
}
