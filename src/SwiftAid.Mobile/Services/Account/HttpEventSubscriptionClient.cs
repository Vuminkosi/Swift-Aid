using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Mobile.Services.Account;

/// <summary>Authenticated session-subscription requests through the Gateway cookie/bearer session.</summary>
public sealed class HttpEventSubscriptionClient(HttpClient client) : ISubscriptionClient
{
    private const string Capability = "session-subscriptions";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<UiCapabilityResult<IReadOnlyList<SubscriptionView>>> ListMineAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/v1/my/session-subscriptions", null, cancellationToken)
            .ConfigureAwait(false);
        if (response is null) return Retryable<IReadOnlyList<SubscriptionView>>();
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return new UiCapabilityResult<IReadOnlyList<SubscriptionView>>(UiCapabilityResultCode.Denied, null,
                Metadata(false), "Sign in to view your reminders.");
        if (!response.IsSuccessStatusCode) return Retryable<IReadOnlyList<SubscriptionView>>();
        var values = await ReadAsync<EventSubscription[]>(response, cancellationToken).ConfigureAwait(false);
        return values is null || values.Any(value => !IsValid(value))
            ? Retryable<IReadOnlyList<SubscriptionView>>()
            : Success<IReadOnlyList<SubscriptionView>>(values.Select(ToView).ToArray());
    }

    public async Task<UiCapabilityResult<SubscriptionOptions>> GetSubscriptionOptionsAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sessionId:D}/subscription/options", null, cancellationToken)
            .ConfigureAwait(false);
        if (response is null) return Retryable<SubscriptionOptions>();
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return new UiCapabilityResult<SubscriptionOptions>(UiCapabilityResultCode.Denied, null,
                Metadata(false), "Sign in to manage event reminders.");
        if (!response.IsSuccessStatusCode) return Retryable<SubscriptionOptions>();
        var value = await ReadAsync<EventSubscriptionOptions>(response, cancellationToken).ConfigureAwait(false);
        if (value is null || value.SessionId != sessionId || value.AvailableChannels is null || value.SessionVersion < 1 ||
            string.IsNullOrWhiteSpace(value.ConsentReference) || string.IsNullOrWhiteSpace(value.ConsentText))
            return Retryable<SubscriptionOptions>();
        var channels = new List<SubscriptionChannel>(value.AvailableChannels.Count);
        foreach (var channel in value.AvailableChannels)
        {
            if (!Enum.TryParse(channel, ignoreCase: true, out SubscriptionChannel parsed) ||
                !Enum.IsDefined(parsed)) return Retryable<SubscriptionOptions>();
            channels.Add(parsed);
        }

        return Success(new SubscriptionOptions(value.SessionId, value.ConsentReference, value.SessionVersion,
            value.ConsentText, channels, value.IsCurrent));
    }

    public async Task<UiCapabilityResult<SubscriptionView>> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sessionId:D}/subscription", null, cancellationToken)
            .ConfigureAwait(false);
        if (response is null) return Retryable<SubscriptionView>();
        if (response.StatusCode == HttpStatusCode.Unauthorized) return SignedOut<SubscriptionView>();
        if (!response.IsSuccessStatusCode) return Retryable<SubscriptionView>();
        var value = await ReadAsync<EventSubscription>(response, cancellationToken).ConfigureAwait(false);
        return !IsValid(value) || value!.SessionId != sessionId ? Retryable<SubscriptionView>() : Success(ToView(value));
    }

    public async Task<UiCapabilityResult<SubscriptionView>> SubscribeAsync(
        SubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Channel is { } channel
            ? new EventSubscriptionEndpointsPayload(request.ExpectedVersion, request.ConsentReference, channel.ToString())
            : null;
        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/sessions/{request.SessionId:D}/subscription", body, cancellationToken)
            .ConfigureAwait(false);
        return await ReadMutationAsync(response, request.SessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UiCapabilityResult<SubscriptionView>> UnsubscribeAsync(
        Guid sessionId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId:D}/subscription/unsubscribe",
            new UnsubscribeRequest(expectedVersion), cancellationToken).ConfigureAwait(false);
        return await ReadMutationAsync(response, sessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UiCapabilityResult<SubscriptionView>> ReadMutationAsync(
        HttpResponseMessage? response, Guid sessionId, CancellationToken cancellationToken)
    {
        if (response is null) return Retryable<SubscriptionView>();
        if (response.StatusCode == HttpStatusCode.Unauthorized) return SignedOut<SubscriptionView>();
        if (response.StatusCode == HttpStatusCode.Conflict)
            return new UiCapabilityResult<SubscriptionView>(UiCapabilityResultCode.Conflict, null,
                Metadata(isAvailable: true), "The session or subscription changed. Refresh before trying again.");
        if (!response.IsSuccessStatusCode) return Retryable<SubscriptionView>();
        var value = await ReadAsync<EventSubscription>(response, cancellationToken).ConfigureAwait(false);
        return !IsValid(value) || value!.SessionId != sessionId ? Retryable<SubscriptionView>() : Success(ToView(value));
    }

    private async Task<HttpResponseMessage?> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (method != HttpMethod.Get) request.Content = JsonContent.Create(body, options: JsonOptions);
            return await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException) { return default; }
    }

    private static SubscriptionView ToView(EventSubscription subscription) => new(subscription.SessionId,
        subscription.Status == EventSubscriptionStatus.Active ? SubscriptionStatus.Active : SubscriptionStatus.Unsubscribed,
        subscription.Version, false,
        subscription.Status == EventSubscriptionStatus.Active && subscription.Channels.Count > 0 &&
        Enum.TryParse(subscription.Channels[0], ignoreCase: true, out SubscriptionChannel channel) && Enum.IsDefined(channel)
            ? channel
            : null, subscription.HasInactiveStoredConsent);

    private static bool IsValid(EventSubscription? value) => value is not null && value.SessionId != Guid.Empty &&
        Enum.IsDefined(value.Status) && value.Version >= 0 && value.Channels is not null &&
        (value.Status != EventSubscriptionStatus.Active || value.Version > 0);

    private static UiCapabilityResult<T> Success<T>(T value) => new(UiCapabilityResultCode.Success, value, Metadata(true));

    private static UiCapabilityResult<T> SignedOut<T>() => new(UiCapabilityResultCode.Denied,
        default, Metadata(false), "Sign in to manage event reminders.");

    private static UiCapabilityResult<T> Retryable<T>() => new(UiCapabilityResultCode.Retryable, default,
        Metadata(false), "Event reminders are temporarily unavailable. Try again.");

    private static UiCapabilityMetadata Metadata(bool isAvailable) => new(Capability,
        UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, isAvailable);

    private sealed record EventSubscriptionEndpointsPayload(long SessionVersion, string ConsentReference, string Channel);
    private sealed record UnsubscribeRequest(long ExpectedVersion);
}
