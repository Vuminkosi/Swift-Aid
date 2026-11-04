using System.Text.Json;
using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Framework function backed by a server-owned Ellai tool invocation.</summary>
public sealed class EllaiFrameworkTool : AIFunction
{
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);
    private readonly Func<EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> _invoke;
    private readonly JsonElement _schema;

    public EllaiFrameworkTool(string name,
        Func<EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        if (!EllaiToolContractCatalog.TryGet(name, out _))
            throw new ArgumentException("Tool is not registered.", nameof(name));

        Name = name;
        _invoke = invoke;
        _schema = JsonSerializer.Deserialize<JsonElement>(EllaiToolContractCatalog.ToJsonSchema(name));
    }

    public override string Name { get; }

    public override string Description => EllaiToolBroker.DescribeTool(Name);

    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var converted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            var text = value switch
            {
                string str => str,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null
            };
            if (text is null)
                throw new ArgumentException("Tool arguments must be strings.", nameof(arguments));
            converted.Add(key, text);
        }

        if (!EllaiToolContractCatalog.TryValidateArguments(Name, converted, out _))
            throw new ArgumentException("Invalid tool arguments.", nameof(arguments));

        var callId = FunctionInvokingChatClient.CurrentContext?.CallContent?.CallId;
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _invoke(new EllaiToolRequest(Name, converted, callId), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var serialized = JsonSerializer.Serialize(EllaiToolBroker.ApplyBounds(result), ResultJson);
        cancellationToken.ThrowIfCancellationRequested();
        return serialized;
    }
}
