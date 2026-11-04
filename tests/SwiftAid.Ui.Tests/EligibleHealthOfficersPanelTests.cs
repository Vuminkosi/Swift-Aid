#pragma warning disable BL0005 // Exercise case changes and delayed results without a renderer.
using System.Reflection;
using SwiftAid.Contracts.Cases;
using SwiftAid.UI.Components.Surfaces.Operations.CaseDetail;

namespace SwiftAid.Ui.Tests;

public sealed class EligibleHealthOfficersPanelTests
{
    [Fact]
    public async Task LateResultFromAnotherCaseIsDiscarded()
    {
        var client = new DelayedClient();
        using var component = new EligibleHealthOfficersPanel { CaseId = Guid.NewGuid(), Client = client };
        Invoke(component, "OnParametersSet");
        var pending = (Task)Invoke(component, "LoadAsync")!;
        component.CaseId = Guid.NewGuid();
        Invoke(component, "OnParametersSet");
        client.Completion.SetResult(new(EligibleHealthOfficerLookupStatus.Available,
            new([new(Guid.NewGuid(), 1, "other-case-officer", "Officer")], false)));
        await pending;
        Assert.Null(Field(component, "_page"));
        Assert.Null(Field(component, "_message"));
        Assert.Equal(false, Field(component, "_loading"));
    }

    private static object? Invoke(object instance, string method) => instance.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);
    private static object? Field(object instance, string name) => instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);

    private sealed class DelayedClient : IEligibleHealthOfficerClient
    {
        public TaskCompletionSource<EligibleHealthOfficerLookupResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<EligibleHealthOfficerLookupResult> GetAsync(Guid caseId, CancellationToken cancellationToken = default) => Completion.Task;
    }
}
