using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;

namespace SwiftAid.Client.Presentation.PublicWorkflows;

public sealed record SessionSubscriptionDetails(Guid SessionId, long Version, string? Title, DateTimeOffset? StartsAtUtc, string? TimeZoneId, string? ChannelLabel, bool HasInactiveStoredConsent = false);

/// <summary>Resolves each owned subscription through the public detail boundary.</summary>
public static class SessionSubscriptionLookup
{
    public static async Task<IReadOnlyList<SessionSubscriptionDetails>> ResolveAsync(
        IEnumerable<SubscriptionView> subscriptions,
        IPublicCommunitySessionClient sessions,
        CancellationToken cancellationToken = default)
    {
        var active = subscriptions.Where(item => item.Status == SubscriptionStatus.Active || item.HasInactiveStoredConsent).ToArray();
        var results = new SessionSubscriptionDetails[active.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, active.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var item = active[index];
                SwiftAid.Contracts.Sessions.PublicSessionView? session = null;
                try
                {
                    var result = await sessions.GetPublicAsync(item.SessionId, token).ConfigureAwait(false);
                    if (result.Succeeded && result.Value?.SessionId == item.SessionId)
                        session = result.Value;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // The subscription can still be managed when public detail is unavailable.
                }

                results[index] = new SessionSubscriptionDetails(item.SessionId, item.Version,
                    session?.Title, session?.StartUtc, session?.TimeZoneId, item.Channel switch
                    {
                        SubscriptionChannel.Email => "Email reminders",
                        SubscriptionChannel.MobilePush => "Mobile notifications",
                        _ => null
                    }, item.HasInactiveStoredConsent);
            });
        return results;
    }
}
