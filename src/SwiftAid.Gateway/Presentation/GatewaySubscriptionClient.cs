using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using SwiftAid.Application.Sessions;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Uses the authenticated Blazor circuit identity; caller-supplied account IDs are never accepted.</summary>
public sealed class GatewaySubscriptionClient(
    IEventSubscriptionService subscriptions,
    AuthenticationStateProvider authenticationStateProvider) : ISubscriptionClient
{
    private const string Capability = "session-subscriptions";
    private readonly IEventSubscriptionService _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));

    public async Task<UiCapabilityResult<IReadOnlyList<SubscriptionView>>> ListMineAsync(
        CancellationToken cancellationToken = default)
    {
        var actorId = await GetAccountIdAsync(cancellationToken).ConfigureAwait(false);
        if (actorId is null)
            return new UiCapabilityResult<IReadOnlyList<SubscriptionView>>(UiCapabilityResultCode.Denied, null,
                UiCapabilityResults.UnavailableMetadata(Capability, "Sign in to view your reminders."),
                "Sign in to view your reminders.");
        try
        {
            var subscriptions = await _subscriptions.ListMineAsync(actorId, cancellationToken).ConfigureAwait(false);
            return Live<IReadOnlyList<SubscriptionView>>(subscriptions.Select(ToView).ToArray(), Capability);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return UiCapabilityResults.Unavailable<IReadOnlyList<SubscriptionView>>(Capability); }
    }

    public async Task<UiCapabilityResult<SubscriptionOptions>> GetSubscriptionOptionsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var actorId = await GetAccountIdAsync(cancellationToken).ConfigureAwait(false);
        if (actorId is null) return UiCapabilityResults.Unavailable<SubscriptionOptions>("session-subscription-options");
        try
        {
            var options = await _subscriptions.GetOptionsAsync(sessionId, actorId, cancellationToken).ConfigureAwait(false);
            return options is null
                ? UiCapabilityResults.Unavailable<SubscriptionOptions>("session-subscription-options")
                : Live(new SubscriptionOptions(options.SessionId, options.ConsentReference, options.SessionVersion,
                    options.ConsentText, options.AvailableChannels.Select(ParseChannel).ToArray(), options.IsCurrent),
                    "session-subscription-options");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return UiCapabilityResults.Unavailable<SubscriptionOptions>("session-subscription-options"); }
    }

    public async Task<UiCapabilityResult<SubscriptionView>> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var actorId = await GetAccountIdAsync(cancellationToken).ConfigureAwait(false);
        if (actorId is null)
            return Live(new SubscriptionView(sessionId, SubscriptionStatus.RequiresAuthentication, 0, false), Capability);
        try
        {
            var subscription = await _subscriptions.GetAsync(sessionId, actorId, cancellationToken).ConfigureAwait(false);
            return subscription is null
                ? UiCapabilityResults.Unavailable<SubscriptionView>(Capability)
                : Live(ToView(subscription), Capability);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return UiCapabilityResults.Unavailable<SubscriptionView>(Capability); }
    }

    public async Task<UiCapabilityResult<SubscriptionView>> SubscribeAsync(
        SubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actorId = await GetAccountIdAsync(cancellationToken).ConfigureAwait(false);
        if (actorId is null)
            return Live(new SubscriptionView(request.SessionId, SubscriptionStatus.RequiresAuthentication, 0, false), Capability);
        if (request.Channel is not { } channel)
            return new UiCapabilityResult<SubscriptionView>(UiCapabilityResultCode.InvalidRequest, null,
                UiCapabilityResults.UnavailableMetadata(Capability, "Select a reminder channel."), "Select a reminder channel.");
        try
        {
            var subscription = await _subscriptions.SubscribeAsync(request.SessionId, actorId, request.ExpectedVersion,
                request.ConsentReference, channel.ToString(), cancellationToken).ConfigureAwait(false);
            return Live(ToView(subscription), Capability);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SessionSubscriptionConflictException)
        {
            return new UiCapabilityResult<SubscriptionView>(UiCapabilityResultCode.Conflict, null,
                UiCapabilityResults.UnavailableMetadata(Capability, "The session or subscription changed. Refresh before trying again."),
                "The session or subscription changed. Refresh before trying again.");
        }
        catch (ArgumentException)
        {
            return new UiCapabilityResult<SubscriptionView>(UiCapabilityResultCode.InvalidRequest, null,
                UiCapabilityResults.UnavailableMetadata(Capability, "Review the reminder consent and channel."),
                "Review the reminder consent and channel.");
        }
        catch (Exception) { return UiCapabilityResults.Unavailable<SubscriptionView>(Capability); }
    }

    public async Task<UiCapabilityResult<SubscriptionView>> UnsubscribeAsync(
        Guid sessionId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var actorId = await GetAccountIdAsync(cancellationToken).ConfigureAwait(false);
        if (actorId is null)
            return Live(new SubscriptionView(sessionId, SubscriptionStatus.RequiresAuthentication, 0, false), Capability);
        try
        {
            var subscription = await _subscriptions.UnsubscribeAsync(sessionId, actorId, expectedVersion, cancellationToken)
                .ConfigureAwait(false);
            return Live(ToView(subscription), Capability);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SessionSubscriptionConflictException)
        {
            return new UiCapabilityResult<SubscriptionView>(UiCapabilityResultCode.Conflict, null,
                UiCapabilityResults.UnavailableMetadata(Capability, "The subscription changed. Refresh before trying again."),
                "The subscription changed. Refresh before trying again.");
        }
        catch (Exception) { return UiCapabilityResults.Unavailable<SubscriptionView>(Capability); }
    }

    private async Task<string?> GetAccountIdAsync(CancellationToken cancellationToken)
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var principal = state.User;
        if (principal.Identity?.IsAuthenticated != true) return null;
        var accountId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(accountId) ? null : accountId;
    }

    private static SubscriptionView ToView(EventSubscription subscription) => new(subscription.SessionId,
        subscription.Status == EventSubscriptionStatus.Active ? SubscriptionStatus.Active : SubscriptionStatus.Unsubscribed,
        subscription.Version, false,
        subscription.Status == EventSubscriptionStatus.Active && subscription.Channels.Count > 0
            ? ParseChannel(subscription.Channels[0])
            : null);

    private static SubscriptionChannel ParseChannel(string channel) => Enum.Parse<SubscriptionChannel>(channel, ignoreCase: false);

    private static UiCapabilityResult<T> Live<T>(T value, string capability) => new(
        UiCapabilityResultCode.Success,
        value,
        new UiCapabilityMetadata(capability, UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow, IsAvailable: true));
}
