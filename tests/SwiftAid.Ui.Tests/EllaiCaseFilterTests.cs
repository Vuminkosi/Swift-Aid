using Microsoft.AspNetCore.Components;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Components.Surfaces.Operations;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiCaseFilterTests
{
    [Theory]
    [InlineData("Change her age to 26.", 26)]
    [InlineData("Correct the patient's age to 0", 0)]
    [InlineData("The patient is 24 years old.", 24)]
    [InlineData("She is 24 years old.", 24)]
    [InlineData("30-year-old patient", 30)]
    public void ReportDraftContextAppliesOnlyAnExplicitAgeInTheSameUtterance(string utterance, int expectedAge)
    {
        var context = new EllaiPublicReportDraftContext();
        var owner = new object();
        var applied = -1;
        context.Register(owner, age => { applied = age; return true; }, _ => false);

        Assert.True(context.ApplyAgeCorrection(utterance));
        Assert.Equal(expectedAge, applied);
        Assert.False(context.ApplyAgeCorrection("Set her age to twenty six"));
        Assert.False(context.ApplyAgeCorrection("Change her age to 26 and the symptoms"));
        context.Remove(new object());
        Assert.True(context.ApplyAgeCorrection("Change her age to 27"));
        context.Remove(owner);
        Assert.False(context.ApplyAgeCorrection("Change her age to 28"));
    }

    private static UiIntentProposal Proposal(string key, string value) => new(UiIntentKind.SetPermittedFilter,
        "case-list", null, "case-list." + key, new Dictionary<string, string> { ["value"] = value });
    private static OperationsCaseQueue Create(Func<OperationsCaseQueue.FilterRequestView, Task> changed)
    {
        var component = new OperationsCaseQueue();
        Set(component, nameof(component.State), OperationsSurfaceState.Populated);
        Set(component, nameof(component.OnQueryChanged), EventCallback.Factory.Create<OperationsCaseQueue.FilterRequestView>(new object(), changed));
        Set(component, nameof(component.FilterOptions), new OperationsCaseQueue.FilterOptionsView(
            [new("Human", "Human"), new("Animal", "Animal")], [new("open", "Open")], [new("SZ", "Eswatini")],
            [new("manzini", "Manzini")], [], [], true, true, true, true, true, false, false, true));
        return component;
    }
    private static void Set(OperationsCaseQueue component, string property, object value) => typeof(OperationsCaseQueue).GetProperty(property)!.SetValue(component, value);

    [Fact]
    public async Task CountryUsesCurrentOptionAndResetsRegionAndPage()
    {
        OperationsCaseQueue.FilterRequestView? observed = null;
        using var component = Create(query => { observed = query; return Task.CompletedTask; });
        Set(component, nameof(component.Query), component.Query with { PageNumber = 4, RegionKey = "old-region", Search = "keep-search" });
        Assert.True(await component.ApplyEllaiFilterAsync(Proposal("country", "Eswatini"), CancellationToken.None));
        Assert.Equal("SZ", observed!.CountryCode);
        Assert.Null(observed.RegionKey);
        Assert.Equal(1, observed.PageNumber);
        Assert.Equal("keep-search", observed.Search);
    }

    [Theory]
    [InlineData("Human", OperationsCaseQueue.SectorKind.Human)]
    [InlineData("human", OperationsCaseQueue.SectorKind.Human)]
    [InlineData("Animal", OperationsCaseQueue.SectorKind.Animal)]
    [InlineData("animal", OperationsCaseQueue.SectorKind.Animal)]
    [InlineData("Unknown", OperationsCaseQueue.SectorKind.Unknown)]
    public async Task SectorOptionsPreserveExplicitUnknownAndAcceptClientKeyCasing(string key, OperationsCaseQueue.SectorKind expected)
    {
        OperationsCaseQueue.FilterRequestView? observed = null;
        using var component = Create(query => { observed = query; return Task.CompletedTask; });
        Set(component, nameof(component.FilterOptions), component.FilterOptions! with
        { Sectors = [new(key, "Selected sector")] });
        Set(component, nameof(component.Query), component.Query with { PageNumber = 3 });
        Assert.True(await component.ApplyEllaiFilterAsync(Proposal("sector", key), CancellationToken.None));
        Assert.Equal(expected, observed!.Sector);
        Assert.Equal(1, observed.PageNumber);
    }

    [Theory]
    [InlineData("country", "Zimbabwe")]
    [InlineData("assignee", "invented-person")]
    [InlineData("status", "invented-status")]
    [InlineData("from", "yesterday")]
    [InlineData("sort", "arbitrary")]
    public async Task UnavailableOrInvalidFiltersNeverCallQuery(string key, string value)
    {
        var calls = 0;
        using var component = Create(_ => { calls++; return Task.CompletedTask; });
        Assert.False(await component.ApplyEllaiFilterAsync(Proposal(key, value), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AmbiguousLabelsAndDeniedOrDisposedComponentsFailClosed()
    {
        var calls = 0;
        var component = Create(_ => { calls++; return Task.CompletedTask; });
        Set(component, nameof(component.FilterOptions), component.FilterOptions! with
        { Countries = [new("SZ", "Duplicate"), new("ZW", "Duplicate")] });
        Assert.False(await component.ApplyEllaiFilterAsync(Proposal("country", "Duplicate"), CancellationToken.None));
        Set(component, nameof(component.State), OperationsSurfaceState.Denied);
        Assert.False(await component.ApplyEllaiFilterAsync(Proposal("search", "text"), CancellationToken.None));
        Set(component, nameof(component.State), OperationsSurfaceState.Populated);
        component.Dispose();
        Assert.False(await component.ApplyEllaiFilterAsync(Proposal("search", "text"), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DateRangeValidationPreservesCurrentQuery()
    {
        var calls = 0;
        using var component = Create(_ => { calls++; return Task.CompletedTask; });
        Set(component, nameof(component.Query), component.Query with { UpdatedToUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z") });
        Assert.False(await component.ApplyEllaiFilterAsync(Proposal("from", "2026-01-02"), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ConcurrentFilterRequestsDoNotOverlapQueryCallbacks()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var component = Create(_ => release.Task);
        var pending = component.ApplyEllaiFilterAsync(Proposal("search", "first"), CancellationToken.None);
        Assert.False(await component.ApplyEllaiFilterAsync(Proposal("search", "second"), CancellationToken.None));
        release.SetResult();
        Assert.True(await pending);
    }

    [Fact]
    public async Task MountedContextRejectsAmbiguityAndRemovedOwners()
    {
        var context = new EllaiCaseFilterContext();
        var calls = 0;
        var first = new object(); var second = new object();
        Task<bool> Apply(UiIntentProposal _, CancellationToken ct) { calls++; return Task.FromResult(true); }
        context.Register(first, Apply); context.Register(second, Apply);
        Assert.False(await context.ApplyAsync(Proposal("search", "text"), CancellationToken.None));
        context.Remove(second);
        Assert.True(await context.ApplyAsync(Proposal("search", "text"), CancellationToken.None));
        context.Remove(first);
        Assert.False(await context.ApplyAsync(Proposal("search", "text"), CancellationToken.None));
        Assert.Equal(1, calls);
    }
}
