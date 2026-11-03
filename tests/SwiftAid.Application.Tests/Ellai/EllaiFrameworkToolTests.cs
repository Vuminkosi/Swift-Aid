using System.Text.Json;
using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiFrameworkToolTests
{
    [Fact]
    public async Task UsesCanonicalSchemaAndPreservesBrokerDenial()
    {
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService());
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true);
        var tool = new EllaiFrameworkTool(EllaiToolNames.State,
            (request, ct) => broker.InvokeAsync(context, request, ct));

        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(
            EllaiToolContractCatalog.ToJsonSchema(EllaiToolNames.State)), tool.JsonSchema));
        var result = Assert.IsType<EllaiToolResult>(await tool.InvokeAsync(new AIFunctionArguments()));
        Assert.False(result.Allowed);
        Assert.Equal(EllaiToolReasonCodes.ConsentMissing, result.ReasonCode);
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
