using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Sessions;
using SwiftAid.UI.Components.Surfaces.Account;

namespace SwiftAid.Ui.Tests;

public sealed class SessionSubscriptionLookupTests
{
    [Fact]
    public async Task ResolvesEveryActiveSubscriptionAndKeepsUnavailableOnesManageable()
    {
        var found = Guid.NewGuid();
        var unavailable = Guid.NewGuid();
        var inactive = Guid.NewGuid();
        var client = new DetailClient(found);
        var summaries = await SessionSubscriptionLookup.ResolveAsync(
            [new(found, SubscriptionStatus.Active, 7, false, SubscriptionChannel.Email),
             new(unavailable, SubscriptionStatus.Active, 8, false),
             new(inactive, SubscriptionStatus.Unsubscribed, 9, false)], client);

        Assert.Equal(2, summaries.Count);
        Assert.Equal("Workshop", summaries[0].Title);
        Assert.Equal("Email reminders", summaries[0].ChannelLabel);
        Assert.Null(summaries[1].Title);
        Assert.Equal(8, summaries[1].Version);
        Assert.Equal(2, client.Calls);
    }

    private sealed class DetailClient(Guid found) : IPublicCommunitySessionClient
    {
        public int Calls;

        public Task<UiCapabilityResult<PublicSessionView>> GetPublicAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (sessionId != found) throw new InvalidOperationException("Offline");
            var view = new PublicSessionView(sessionId, 1, "Workshop", "Summary", "Community", [], "SZ", "Region", "Education", "Presenter", "en", DateTimeOffset.UtcNow, "UTC", 60, null, [], SessionChannelType.None, null, "Scheduled", null, 0);
            var metadata = new UiCapabilityMetadata("sessions", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true);
            return Task.FromResult(new UiCapabilityResult<PublicSessionView>(UiCapabilityResultCode.Success, view, metadata));
        }

        public Task<UiCapabilityResult<IReadOnlyList<PublicSessionView>>> ListPublicAsync(int? limit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiCapabilityResult<PublicSessionPage>> QueryPublicAsync(PublicSessionQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiCapabilityResult<string>> GetInvitationTextAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
