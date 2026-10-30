using SwiftAid.UI.Ellai;

namespace SwiftAid.Ui.Tests;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T08")]
public sealed class EllaiApprovalDialogModelTests
{
    private static EllaiApprovalDialogModel Model() => new(new[]
    {
        new EllaiApprovalActionRow("a-1", "Create administrative review task", "Target case-1", false),
        new EllaiApprovalActionRow("a-2", "Queue internal reminder", "Obligation o-1", false),
    });

    [Fact]
    public void ApproveRequiresExplicitSelection()
    {
        var model = Model();
        Assert.False(model.CanApprove);
        Assert.Empty(model.SelectedIds);

        model.Toggle("a-1");
        Assert.True(model.CanApprove);
        Assert.Equal(new[] { "a-1" }, model.SelectedIds);

        model.Toggle("a-1");
        Assert.False(model.CanApprove);
    }

    [Fact]
    public void NothingIsPreselected()
    {
        var model = new EllaiApprovalDialogModel(new[]
        {
            new EllaiApprovalActionRow("a-1", "Label", "Detail", true),
        });

        Assert.False(model.CanApprove);
        Assert.Empty(model.SelectedIds);
    }

    [Fact]
    public void UnknownActionIdIsIgnored()
    {
        var model = Model();
        model.Toggle("a-missing");
        Assert.False(model.CanApprove);
    }

    [Fact]
    public void FreshModelStartsUnselected()
    {
        var first = Model();
        first.Toggle("a-1");
        first.Toggle("a-2");
        Assert.Equal(2, first.SelectedIds.Count);

        var revised = new EllaiApprovalDialogModel(first.Rows);
        Assert.False(revised.CanApprove);
        Assert.Empty(revised.SelectedIds);
        Assert.Equal(2, revised.Rows.Count);
    }
}
