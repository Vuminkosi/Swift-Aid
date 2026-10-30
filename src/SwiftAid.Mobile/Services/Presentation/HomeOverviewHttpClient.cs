using System.Net;
using System.Text.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;
using SwiftAid.Mobile.Services.Http;

namespace SwiftAid.Mobile.Services.Presentation;

/// <summary>Privacy-minimized home overview through the gateway cookie session.</summary>
public sealed class HomeOverviewHttpClient(CookieSessionHttpClient session) : IMobileHomeOverviewClient
{
    private const string Capability = "mobile-home-overview";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CookieSessionHttpClient _session = session ?? throw new ArgumentNullException(nameof(session));

    public async Task<UiCapabilityResult<MobileHomeOverview>> GetAsync(
        MobileHomeOverviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (!query.IsValid)
        {
            return Invalid("The requested home summary is unavailable.");
        }

        try
        {
            using var response = await _session.GetAsync(
                $"/api/v1/home/overview?recentActivityLimit={query.RecentActivityLimit}",
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Denied();
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return Invalid("The requested home summary is unavailable.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Retryable("Your work summary is temporarily unavailable.");
            }

            var payload = await ReadAsync<HomeOverviewResult>(response, cancellationToken).ConfigureAwait(false);
            var overview = payload is { Access: HomeOverviewAccess.Allowed, Payload: not null }
                ? ToOverview(payload.Payload)
                : null;
            return overview is { IsValid: true }
                ? Success(overview)
                : Retryable("Your work summary is temporarily unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable("Your work summary is temporarily unavailable.");
        }
    }

    private static MobileHomeOverview? ToOverview(HomeOverviewPayload payload)
    {
        if (payload.Counts is null || payload.Activities is null ||
            payload.Counts.Count != Enum.GetValues<MobileHomeWorkCountKind>().Length)
        {
            return null;
        }

        var counts = new List<MobileHomeWorkCount>();
        foreach (var count in payload.Counts)
        {
            if (!TryGetKind(count.Kind, out var kind))
            {
                return null;
            }

            if (count.Value is null)
            {
                if (count.Reason is null || !Enum.IsDefined(typeof(HomeOverviewCountReason), count.Reason))
                {
                    return null;
                }

                counts.Add(new MobileHomeWorkCount(
                    kind,
                    MobileHomeValueState.Unavailable,
                    null,
                    ToReason(count.Reason.Value)));
                continue;
            }

            if (count.Value < 0 || count.Reason is not null)
            {
                return null;
            }

            counts.Add(new MobileHomeWorkCount(kind, MobileHomeValueState.Available, count.Value, null));
        }

        var activities = new List<MobileHomeActivity>();
        foreach (var activity in payload.Activities)
        {
            var mapped = ToActivity(activity);
            if (mapped is null)
            {
                return null;
            }

            activities.Add(mapped);
        }

        return new MobileHomeOverview(
            payload.DisplayName,
            payload.WorkspaceLabel,
            null,
            payload.AsOfUtc,
            payload.FreshnessExpiresAtUtc,
            MobileHomeFreshnessState.Current,
            payload.Partial ? MobileHomeCompleteness.Partial : MobileHomeCompleteness.Complete,
            counts,
            activities);
    }

    private static MobileHomeActivity? ToActivity(HomeOverviewActivity activity)
    {
        var (kind, destination) = activity.Kind switch
        {
            HomeOverviewActivityKind.CaseReported =>
                (MobileHomeActivityKind.CaseReported, MobileHomeActivityDestination.Cases),
            HomeOverviewActivityKind.AssignmentReceived =>
                (MobileHomeActivityKind.AssignmentReceived, MobileHomeActivityDestination.Cases),
            HomeOverviewActivityKind.EscalationOpened =>
                (MobileHomeActivityKind.EscalationOpened, MobileHomeActivityDestination.Cases),
            _ => (null as MobileHomeActivityKind?, MobileHomeActivityDestination.Cases)
        };

        if (kind is null || activity.CaseId == Guid.Empty)
        {
            return null;
        }

        return new MobileHomeActivity(
            activity.ActivityId,
            kind.Value,
            activity.OccurredAtUtc,
            destination,
            activity.CaseId.ToString("D"));
    }

    private static bool TryGetKind(HomeOverviewCountKind kind, out MobileHomeWorkCountKind mapped)
    {
        mapped = kind switch
        {
            HomeOverviewCountKind.AssignedCases => MobileHomeWorkCountKind.AssignedCases,
            HomeOverviewCountKind.DueFollowUps => MobileHomeWorkCountKind.DueFollowUps,
            HomeOverviewCountKind.OverdueFollowUps => MobileHomeWorkCountKind.OverdueFollowUps,
            HomeOverviewCountKind.UnresolvedAssistanceRequests =>
                MobileHomeWorkCountKind.UnresolvedAssistanceRequests,
            HomeOverviewCountKind.UnresolvedEscalations => MobileHomeWorkCountKind.UnresolvedEscalations,
            _ => (MobileHomeWorkCountKind)(-1)
        };

        return Enum.IsDefined(mapped);
    }

    private static MobileHomeDataReason ToReason(HomeOverviewCountReason reason) => reason switch
    {
        HomeOverviewCountReason.NotCollected => MobileHomeDataReason.NotCollected,
        _ => MobileHomeDataReason.SourceUnavailable
    };

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static UiCapabilityResult<MobileHomeOverview> Invalid(string message) => new(
        UiCapabilityResultCode.InvalidRequest, default, Metadata(), message);

    private static UiCapabilityResult<MobileHomeOverview> Denied() => new(
        UiCapabilityResultCode.Denied,
        default,
        Metadata(),
        "Sign in to view your work summary.");

    private static UiCapabilityResult<MobileHomeOverview> Retryable(string message) => new(
        UiCapabilityResultCode.Retryable, default, Metadata(), message);

    private static UiCapabilityResult<MobileHomeOverview> Success(MobileHomeOverview value) => new(
        UiCapabilityResultCode.Success, value, Metadata(), null);

    private static UiCapabilityMetadata Metadata() => new(
        Capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        IsAvailable: true);
}
