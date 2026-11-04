using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Creates framework functions from the server-owned session tool profile.</summary>
public static class EllaiFrameworkToolCatalog
{
    public static IReadOnlyList<AITool> Create(EllaiToolContext context,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invoke);

        // Take a snapshot so later mutations cannot expand the agent's catalogue.
        var allowed = context.AllowedTools is null
            ? null
            : new HashSet<string>(context.AllowedTools, StringComparer.Ordinal);
        var boundContext = context with { AllowedTools = allowed };
        return EllaiToolContractCatalog.All.Keys
            .Where(name => allowed is null || allowed.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => (AITool)new EllaiFrameworkTool(name,
                (request, ct) => invoke(boundContext, request, ct)))
            .ToArray();
    }
}
