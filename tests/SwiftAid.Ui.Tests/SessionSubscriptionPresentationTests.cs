using SwiftAid.Client.Presentation;

namespace SwiftAid.Ui.Tests;

public sealed class SessionSubscriptionPresentationTests
{
    [Theory]
    [InlineData(SubscriptionStatus.RequiresAuthentication, SubscriptionStatus.RequiresAuthentication)]
    [InlineData(SubscriptionStatus.Active, SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Unsubscribed, SubscriptionStatus.Unsubscribed)]
    public void SuccessfulSubscriptionResultPreservesItsAuthenticationAndReminderState(
        SubscriptionStatus status, SubscriptionStatus expected)
    {
        var value = new SubscriptionView(Guid.NewGuid(), status, 0, false);
        var result = new UiCapabilityResult<SubscriptionView>(UiCapabilityResultCode.Success, value,
            UiCapabilityResults.UnavailableMetadata("session-subscriptions", "Test state"));

        Assert.Equal(expected, SessionSubscriptionPresentation.Status(result));
    }

    [Fact]
    public void DeniedResultRequestsAuthentication()
    {
        var result = new UiCapabilityResult<SubscriptionView>(UiCapabilityResultCode.Denied, null,
            UiCapabilityResults.UnavailableMetadata("session-subscriptions", "Test state"));

        Assert.Equal(SubscriptionStatus.RequiresAuthentication, SessionSubscriptionPresentation.Status(result));
    }

    [Fact]
    public void SuccessWithoutSubscriptionDoesNotPretendToBeUnsubscribed()
    {
        var result = new UiCapabilityResult<SubscriptionView>(UiCapabilityResultCode.Success, null,
            UiCapabilityResults.UnavailableMetadata("session-subscriptions", "Test state"));

        Assert.Null(SessionSubscriptionPresentation.Status(result));
    }

    [Theory]
    [InlineData(SubscriptionStatus.RequiresAuthentication, false)]
    [InlineData(SubscriptionStatus.Active, false)]
    [InlineData(SubscriptionStatus.Unsubscribed, true)]
    public void UnsubscribeConfirmationRequiresMatchingUnsubscribedResult(SubscriptionStatus status, bool confirms)
    {
        var sessionId = Guid.NewGuid();
        var value = new SubscriptionView(sessionId, status, 1, false);
        var result = new UiCapabilityResult<SubscriptionView>(UiCapabilityResultCode.Success, value,
            UiCapabilityResults.UnavailableMetadata("session-subscriptions", "Test state"));

        Assert.Equal(confirms, SessionSubscriptionPresentation.UnsubscribeMessage(result, sessionId)
            .Contains("You are unsubscribed", StringComparison.Ordinal));
        Assert.DoesNotContain("You are unsubscribed", SessionSubscriptionPresentation.UnsubscribeMessage(result, Guid.NewGuid()), StringComparison.Ordinal);
    }
}
