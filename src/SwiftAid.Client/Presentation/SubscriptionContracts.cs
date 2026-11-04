namespace SwiftAid.Client.Presentation;

public enum SubscriptionStatus
{
    Active,
    Unsubscribed,
    RequiresAuthentication
}

public enum SubscriptionChannel
{
    Email,
    MobilePush
}

public sealed record SubscriptionView(
    Guid SessionId,
    SubscriptionStatus Status,
    long Version,
    bool IsSynthetic,
    SubscriptionChannel? Channel = null,
    bool HasInactiveStoredConsent = false);

public sealed record SubscriptionRequest(
    Guid SessionId,
    string ConsentReference,
    long ExpectedVersion = 0,
    SubscriptionChannel? Channel = null);

public sealed record SubscriptionOptions(
    Guid SessionId,
    string ConsentReference,
    long Version,
    string ConsentText,
    IReadOnlyList<SubscriptionChannel> Channels,
    bool IsCurrent);

public interface ISubscriptionClient
{
    Task<UiCapabilityResult<IReadOnlyList<SubscriptionView>>> ListMineAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<IReadOnlyList<SubscriptionView>>("session-subscriptions"));
    }

    Task<UiCapabilityResult<SubscriptionOptions>> GetSubscriptionOptionsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<SubscriptionOptions>("session-subscription-options"));
    }

    Task<UiCapabilityResult<SubscriptionView>> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<SubscriptionView>> SubscribeAsync(
        SubscriptionRequest request,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<SubscriptionView>> UnsubscribeAsync(
        Guid sessionId,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>Fail-closed reminder service until an authorized subscription endpoint exists.</summary>
public sealed class UnavailableSubscriptionClient : ISubscriptionClient
{
    public Task<UiCapabilityResult<IReadOnlyList<SubscriptionView>>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<IReadOnlyList<SubscriptionView>>("session-subscriptions"));
    }

    public Task<UiCapabilityResult<SubscriptionOptions>> GetSubscriptionOptionsAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<SubscriptionOptions>("session-subscription-options"));
    }

    public Task<UiCapabilityResult<SubscriptionView>> GetAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Unavailable<SubscriptionView>(cancellationToken);

    public Task<UiCapabilityResult<SubscriptionView>> SubscribeAsync(SubscriptionRequest request, CancellationToken cancellationToken = default) =>
        Unavailable<SubscriptionView>(cancellationToken);

    public Task<UiCapabilityResult<SubscriptionView>> UnsubscribeAsync(Guid sessionId, long expectedVersion, CancellationToken cancellationToken = default) =>
        Unavailable<SubscriptionView>(cancellationToken);

    private static Task<UiCapabilityResult<T>> Unavailable<T>(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<T>("session-subscriptions"));
    }
}
