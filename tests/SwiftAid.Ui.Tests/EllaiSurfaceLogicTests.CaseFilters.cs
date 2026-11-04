using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

public sealed partial class EllaiSurfaceLogicTests
{
    private static UiIntentProposal FilterProposal => new(UiIntentKind.SetPermittedFilter, "case-list", null,
        "case-list.country", new Dictionary<string, string> { ["value"] = "Eswatini" });

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task CaseFilterNeedsNewTurnAndAcceptedObservation(bool newTurn, bool accepted)
    {
        var client = new FakeReviewClient { ObservationHandler = () => Task.FromResult(accepted) };
        using var host = await CreateHost(client, CreateSession(1), "unsent text");
        var context = new EllaiCaseFilterContext();
        var calls = 0;
        context.Register(new object(), (_, _) => { calls++; return Task.FromResult(true); });
        using var services = new ServiceCollection().AddSingleton(context).BuildServiceProvider();
        SetProperty(host, "Services", services);
        SetField(host, "_uiObservationModule", new ObservationJsModule { ControlId = "case-list.country" });
        var turn = new EllaiConversationTurnDto("filter-turn", 1, "Filter Eswatini", "Requested", "Completed", 1, DateTimeOffset.UtcNow)
        { UiProposals = [new("filter-proposal", FilterProposal)] };
        SetField(host, "_lastDurableTurn", turn);
        await InvokeAsync(host, "ObserveGuidanceProposal", turn, newTurn);
        await InvokeAsync(host, "ObserveGuidanceProposal", turn, newTurn);
        Assert.Equal(newTurn && accepted ? 1 : 0, calls);
        Assert.Equal("unsent text", GetField<string>(host, "InputText"));
    }

    [Fact]
    public async Task CaseFilterRejectsObservationFromSupersededTurn()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient { ObservationHandler = () => { started.SetResult(); return release.Task; } };
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        var context = new EllaiCaseFilterContext();
        var calls = 0;
        context.Register(new object(), (_, _) => { calls++; return Task.FromResult(true); });
        using var services = new ServiceCollection().AddSingleton(context).BuildServiceProvider();
        SetProperty(host, "Services", services);
        SetField(host, "_uiObservationModule", new ObservationJsModule { ControlId = "case-list.country" });
        var turn = new EllaiConversationTurnDto("filter-turn", 1, "Filter Eswatini", "Requested", "Completed", 1, DateTimeOffset.UtcNow);
        SetField(host, "_lastDurableTurn", turn);
        SetField(host, "_lastCaseFilterProposalId", "proposal");
        var pending = InvokeAsync(host, "ApplyCaseFilterAsync", "filter-turn", "proposal", FilterProposal);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SetField(host, "_lastDurableTurn", turn with { ClientUtteranceId = "new-turn" });
        release.SetResult(true);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, calls);
    }
}
