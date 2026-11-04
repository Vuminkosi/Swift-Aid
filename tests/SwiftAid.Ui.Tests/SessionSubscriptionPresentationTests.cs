using SwiftAid.Client.Presentation;

namespace SwiftAid.Ui.Tests;

public sealed class SessionSubscriptionPresentationTests
{
    [Fact]
    public void SubscribeRequiresMatchingCurrentLiveConsentAndSupportedChannel()
    {
        var id = Guid.NewGuid();
        var metadata = new UiCapabilityMetadata("session-subscriptions", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true);
        var options = new UiCapabilityResult<SubscriptionOptions>(UiCapabilityResultCode.Success,
            new SubscriptionOptions(id, "consent", 3, "Consent", [SubscriptionChannel.Email], true), metadata);
        Assert.True(SessionSubscriptionPresentation.CanSubscribe(options, id, "consent", 3, SubscriptionChannel.Email));
        Assert.False(SessionSubscriptionPresentation.CanSubscribe(options, Guid.NewGuid(), "consent", 3, SubscriptionChannel.Email));
        Assert.False(SessionSubscriptionPresentation.CanSubscribe(options, id, "other", 3, SubscriptionChannel.Email));
        Assert.False(SessionSubscriptionPresentation.CanSubscribe(options, id, "consent", 4, SubscriptionChannel.Email));
        Assert.False(SessionSubscriptionPresentation.CanSubscribe(options, id, "consent", 3, SubscriptionChannel.MobilePush));
        Assert.False(SessionSubscriptionPresentation.CanSubscribe(options with { Value = options.Value! with { IsCurrent = false } }, id, "consent", 3, SubscriptionChannel.Email));
        Assert.False(SessionSubscriptionPresentation.CanSubscribe(options with { Metadata = metadata with { Provenance = UiCapabilityProvenance.SyntheticDevelopment } }, id, "consent", 3, SubscriptionChannel.Email));
        Assert.False(SessionSubscriptionPresentation.CanSubscribe(options with { Metadata = metadata with { IsAvailable = false } }, id, "consent", 3, SubscriptionChannel.Email));
    }

    [Fact]
    public void ReadAndConsentOptionsMustMatchTheRequestedSession()
    {
        var id = Guid.NewGuid();
        var metadata = new UiCapabilityMetadata("session-subscriptions", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true);
        var read = new UiCapabilityResult<SubscriptionView>(UiCapabilityResultCode.Success,
            new SubscriptionView(id, SubscriptionStatus.Active, 2, false), metadata);
        var options = new UiCapabilityResult<SubscriptionOptions>(UiCapabilityResultCode.Success,
            new SubscriptionOptions(id, "consent", 3, "Consent", [SubscriptionChannel.Email], true), metadata);
        Assert.True(SessionSubscriptionPresentation.IsMatchingRead(read, id));
        Assert.True(SessionSubscriptionPresentation.IsMatchingOptions(options, id));
        Assert.False(SessionSubscriptionPresentation.IsMatchingRead(read, Guid.NewGuid()));
        Assert.False(SessionSubscriptionPresentation.IsMatchingOptions(options, Guid.NewGuid()));
        Assert.False(SessionSubscriptionPresentation.IsMatchingRead(read with { Value = read.Value! with { IsSynthetic = true } }, id));
    }

    [Fact]
    public void UpdateConfirmationRequiresCurrentLiveReceiptForTheRequestedSession()
    {
        var sessionId = Guid.NewGuid();
        var metadata = new UiCapabilityMetadata("session-subscriptions", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true);
        var result = new UiCapabilityResult<SubscriptionView>(UiCapabilityResultCode.Success,
            new SubscriptionView(sessionId, SubscriptionStatus.Active, 3, false), metadata);
        Assert.True(SessionSubscriptionPresentation.IsConfirmedUpdate(result, sessionId, SubscriptionStatus.Active, 2));
        Assert.False(SessionSubscriptionPresentation.IsConfirmedUpdate(result, Guid.NewGuid(), SubscriptionStatus.Active, 2));
        Assert.False(SessionSubscriptionPresentation.IsConfirmedUpdate(result, sessionId, SubscriptionStatus.Unsubscribed, 2));
        Assert.True(SessionSubscriptionPresentation.IsConfirmedUpdate(result, sessionId, SubscriptionStatus.Active, 10));
        var unsubscribed = result with { Value = result.Value! with { Status = SubscriptionStatus.Unsubscribed } };
        Assert.False(SessionSubscriptionPresentation.IsConfirmedUpdate(unsubscribed, sessionId, SubscriptionStatus.Unsubscribed, 4));
        Assert.False(SessionSubscriptionPresentation.IsConfirmedUpdate(result with { Value = result.Value! with { IsSynthetic = true } }, sessionId, SubscriptionStatus.Active, 2));
        Assert.False(SessionSubscriptionPresentation.IsConfirmedUpdate(result with { Metadata = metadata with { IsAvailable = false } }, sessionId, SubscriptionStatus.Active, 2));
    }

    [Fact]
    public void RefreshFailureRemainsVisibleAfterAConfirmedUpdate()
    {
        var feedback = SessionSubscriptionPresentation.UpdateFeedback(true, false, null, "Connection lost");
        Assert.True(feedback.IsError);
        Assert.Contains("was saved", feedback.Message, StringComparison.Ordinal);
        Assert.Contains("could not be verified", feedback.Message, StringComparison.Ordinal);

        var unknown = SessionSubscriptionPresentation.UpdateFeedback(false, false, "Saved", null);
        Assert.True(unknown.IsError);
        Assert.Contains("could not be confirmed", unknown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("was saved", unknown.Message, StringComparison.Ordinal);
    }

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
