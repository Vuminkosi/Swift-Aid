using System.Text.Json;
using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiFrameworkToolTests
{
    [Fact]
    public async Task EveryFrameworkToolUsesItsCanonicalSchemaAndRejectsUnknownArguments()
    {
        foreach (var name in EllaiToolContractCatalog.All.Keys)
        {
            var invoked = false;
            var tool = new EllaiFrameworkTool(name, (_, _) =>
            {
                invoked = true;
                throw new InvalidOperationException("Malformed requests must not reach the broker.");
            });
            var canonical = JsonSerializer.Deserialize<JsonElement>(EllaiToolContractCatalog.ToJsonSchema(name));
            Assert.True(JsonElement.DeepEquals(canonical, tool.JsonSchema), name);
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await tool.InvokeAsync(new AIFunctionArguments { ["unexpected_argument"] = "synthetic" }));
            Assert.False(invoked, name);
        }
    }

    [Fact]
    public async Task CatalogueUsesSnapshotOfServerProfileAndBrokerStillChecksIt()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.ProductFacts };
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: allowed);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService());
        var tools = EllaiFrameworkToolCatalog.Create(context, broker.InvokeAsync);
        allowed.Add(EllaiToolNames.State);

        var function = Assert.IsType<EllaiFrameworkTool>(Assert.Single(tools));
        Assert.Equal(EllaiToolNames.ProductFacts, function.Name);
        Assert.Equal(EllaiToolBroker.DescribeTool(EllaiToolNames.ProductFacts), function.Description);
        var result = Assert.IsType<string>(await function.InvokeAsync(new AIFunctionArguments()));
        using var json = JsonDocument.Parse(result);
        Assert.False(json.RootElement.GetProperty("allowed").GetBoolean());
        Assert.Equal(EllaiToolReasonCodes.ConsentMissing, json.RootElement.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public void CatalogueDoesNotAdvertiseUnknownToolsAndDescriptionsCoverCanonicalContracts()
    {
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { "ellai_sql" });
        Assert.Empty(EllaiFrameworkToolCatalog.Create(context, (ctx, req, ct) => throw new InvalidOperationException()));
        var all = EllaiFrameworkToolCatalog.Create(context with { AllowedTools = null },
            (ctx, req, ct) => throw new InvalidOperationException());
        Assert.Equal(EllaiToolContractCatalog.All.Count, all.Count);
        Assert.All(all, tool => Assert.StartsWith(tool.Name + "(", tool.Description));
    }

    [Fact]
    public async Task UsesCanonicalSchemaAndPreservesBrokerDenial()
    {
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService());
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true);
        var tool = new EllaiFrameworkTool(EllaiToolNames.State,
            (request, ct) => broker.InvokeAsync(context, request, ct));

        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(
            EllaiToolContractCatalog.ToJsonSchema(EllaiToolNames.State)), tool.JsonSchema));
        var result = Assert.IsType<string>(await tool.InvokeAsync(new AIFunctionArguments()));
        using var json = JsonDocument.Parse(result);
        Assert.False(json.RootElement.GetProperty("allowed").GetBoolean());
        Assert.Equal(EllaiToolReasonCodes.ConsentMissing, json.RootElement.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task RejectsMalformedArgumentsBeforeInvocation()
    {
        var invoked = false;
        var tool = new EllaiFrameworkTool(EllaiToolNames.DocsGet, (request, ct) =>
        {
            invoked = true;
            throw new InvalidOperationException();
        });
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await tool.InvokeAsync(new AIFunctionArguments { ["section"] = 42 }));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await tool.InvokeAsync(new AIFunctionArguments { ["unexpected"] = "x" }));
        Assert.False(invoked);
        Assert.Throws<ArgumentException>(() => new EllaiFrameworkTool("ellai_sql", (request, ct) => throw new InvalidOperationException()));
    }

    [Fact]
    public async Task PropagatesCancellationToInvocation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var tool = new EllaiFrameworkTool(EllaiToolNames.Now, (request, ct) => throw new InvalidOperationException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await tool.InvokeAsync(new AIFunctionArguments(), cancellation.Token));
    }
}
