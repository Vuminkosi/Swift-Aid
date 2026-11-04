using SwiftAid.Client.Presentation;

namespace SwiftAid.Ui.TestHost;

public sealed class StaffSubscriptionFixture : ISubscriptionClient
{
    public static readonly Guid SessionId = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
    private static readonly UiCapabilityMetadata Metadata = new("reminders", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true);
    public bool Mismatch { get; set; }
    public int SubscribeCalls { get; private set; }
    private bool _active;
    public Task<UiCapabilityResult<SubscriptionOptions>> GetSubscriptionOptionsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new UiCapabilityResult<SubscriptionOptions>(UiCapabilityResultCode.Success,
            new(Mismatch ? Guid.NewGuid() : sessionId, "consent", 3, "Fixture consent", [SubscriptionChannel.Email], true), Metadata));
    public Task<UiCapabilityResult<SubscriptionView>> GetAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new UiCapabilityResult<SubscriptionView>(UiCapabilityResultCode.Success,
            new(sessionId, _active ? SubscriptionStatus.Active : SubscriptionStatus.Unsubscribed, _active ? 1 : 0, false,
                _active ? SubscriptionChannel.Email : null), Metadata));
    public Task<UiCapabilityResult<SubscriptionView>> SubscribeAsync(SubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        SubscribeCalls++;
        _active = true;
        return GetAsync(request.SessionId, cancellationToken);
    }
    public Task<UiCapabilityResult<SubscriptionView>> UnsubscribeAsync(Guid sessionId, long expectedVersion, CancellationToken cancellationToken = default) =>
        Task.FromResult(UiCapabilityResults.Unavailable<SubscriptionView>("fixture"));
}
