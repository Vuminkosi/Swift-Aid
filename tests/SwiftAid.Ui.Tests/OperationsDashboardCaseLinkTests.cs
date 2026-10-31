using SwiftAid.UI.Components.Surfaces.Operations;

namespace SwiftAid.Ui.Tests;

public sealed class OperationsDashboardCaseLinkTests
{
    [Theory]
    [InlineData("2f47e9fc-94db-4d84-9c94-a841761f9b51", "/case/2f47e9fc-94db-4d84-9c94-a841761f9b51")]
    [InlineData("2F47E9FC94DB4D849C94A841761F9B51", "/case/2f47e9fc-94db-4d84-9c94-a841761f9b51")]
    public void GetCaseHref_ReturnsCanonicalAuthorizedRouteForGuid(
        string caseId,
        string expectedHref)
    {
        Assert.Equal(expectedHref, DashboardCaseView.GetCaseHref(caseId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("../admin")]
    [InlineData("2f47e9fc-94db-4d84-9c94-a841761f9b51?next=/admin")]
    public void GetCaseHref_ReturnsNullForInvalidIdentifier(string? caseId)
    {
        Assert.Null(DashboardCaseView.GetCaseHref(caseId));
    }
}
