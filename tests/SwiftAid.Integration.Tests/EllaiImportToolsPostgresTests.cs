using System.Text.Json;
using System.Security.Claims;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Authorization;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class EllaiImportToolsPostgresTests
{
    [IntakePostgresFact]
    public async Task Live_broker_reads_owner_imports_with_current_scope_and_never_returns_source_text()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var linked = await SharedWorkspaceImportTests.AddOrganizationAsync(fixture.Db);
        var importId = Guid.NewGuid();
        var imported = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, importId, CaseImportPostgresTests.Context,
            1, true, new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
        Assert.Equal(1, imported.Summary!.Accepted);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), imports: fixture.Store, importWorkspaces: fixture.Store);
        var context = Context(CaseImportPostgresTests.Actor);
        var destinations = await broker.InvokeAsync(context, new(EllaiToolNames.ImportDestinations, new Dictionary<string, string>()), default);
        Assert.True(destinations.Allowed);
        Assert.Contains("workspace-1", destinations.ResultSummary);
        Assert.DoesNotContain(linked.Organization, destinations.ResultSummary);

        var history = await broker.InvokeAsync(context, new(EllaiToolNames.Imports, new Dictionary<string, string>
        { ["workspace_id"] = "workspace-1", ["organization_id"] = "org-zw-1", ["country"] = "ZW" }), default);
        Assert.True(history.Allowed);
        Assert.Contains(importId.ToString("D"), history.ResultSummary);
        var request = new EllaiToolRequest(EllaiToolNames.ImportStatus, new Dictionary<string, string> { ["import_id"] = importId.ToString("D") });
        var status = await broker.InvokeAsync(context, request, default);
        Assert.True(status.Allowed);
        Assert.Equal("ImportJob", Assert.Single(status.Evidence).SourceKind);
        Assert.DoesNotContain("Original supplied concern", status.ResultSummary);
        using var document = JsonDocument.Parse(status.ResultSummary);
        Assert.Equal(1, document.RootElement.GetProperty("import").GetProperty("Accepted").GetInt64());
        Assert.Equal("Completed", document.RootElement.GetProperty("import").GetProperty("State").GetString());
        Assert.Equal("not-reported-by-import-ledger", document.RootElement.GetProperty("intelligenceStatus").GetString());

        var other = await broker.InvokeAsync(Context(linked.Actor), request, default);
        Assert.False(other.Allowed);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, other.ReasonCode);
        var staleActor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "officer-1"), new Claim(ActorContext.AccessEpochClaimType, "6")], "fixture")));
        var stale = await broker.InvokeAsync(Context(staleActor), request, default);
        Assert.False(stale.Allowed);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, stale.ReasonCode);
        var injected = await broker.InvokeAsync(context, request with
        { Arguments = new Dictionary<string, string> { ["import_id"] = importId.ToString("D"), ["actor_id"] = linked.Actor.ActorId } }, default);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, injected.ReasonCode);
        Assert.False((await broker.InvokeAsync(context with { RemainingCalls = 0 }, request, default)).Allowed);
        Assert.False((await broker.InvokeAsync(context with { Actor = null }, request, default)).Allowed);
        Assert.False((await broker.InvokeAsync(context with { AllowedTools = new HashSet<string> { EllaiToolNames.ProductFacts } }, request, default)).Allowed);
    }

    private static EllaiToolContext Context(ActorContext actor) => new("import-session", "import-scope", "public.product-help", null, 4, true,
        Actor: actor, DataClassification: EllaiProcessingClassification.UserProvidedNonSensitive,
        ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose);
}
