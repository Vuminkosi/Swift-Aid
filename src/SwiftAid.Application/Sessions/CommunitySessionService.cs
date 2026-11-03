using SwiftAid.Contracts.Sessions;
using SwiftAid.Domain.Sessions;
using DomainChannel = SwiftAid.Domain.Sessions.SessionChannelType;
using DomainMaterial = SwiftAid.Domain.Sessions.SessionMaterialRef;
using DomainVisibility = SwiftAid.Domain.Sessions.SessionVisibility;

namespace SwiftAid.Application.Sessions;

/// <summary>
/// Officer community-session service (P10.1). Staff authorization is enforced by the
/// API layer; the service trusts the passed actorId. Org-scoped session permissions
/// are a recorded limitation, not enforced here.
/// No social-platform API calls anywhere: sharing is copy-text, the native share
/// sheet, or opening the pasted destination link (AT-29, SA-SES-04).
/// Reminder dispatch/execution is OUT of scope (P6.3 durable-reminder lane); this
/// service only (re)computes occurrence rows and suppresses them on cancel.
/// </summary>
public sealed class CommunitySessionService(ICommunitySessionStore store, ISessionMaterialEligibility? materialEligibility = null) : ICommunitySessionService
{
    public const int DefaultPublicLimit = 50;
    public const int MaxPublicLimit = 100;

    private readonly ICommunitySessionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ISessionMaterialEligibility? _materialEligibility = materialEligibility;

    public async Task<SessionCommandResult> ExecuteAsync(SessionCommand command, string actorId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new ArgumentException("A staff actor id is required.", nameof(actorId));
        }

        if (command is CreateSessionDraftCommand create)
        {
            return await ExecuteCreateAsync(create, actorId, ct).ConfigureAwait(false);
        }

        var session = await _store.LoadAsync(command.SessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            throw new KeyNotFoundException($"Community session {command.SessionId:D} was not found.");
        }

        if (session.Version != command.ExpectedVersion)
        {
            throw new SessionVersionConflictException(command.SessionId, command.ExpectedVersion, session.Version);
        }

        switch (command)
        {
            case ReviewSessionCommand review:
                await RequireEligibleMaterialsAsync(session, session.Materials.Select(ToContractMaterial).ToArray(),
                    ToContractVisibility(session.Visibility), session.Language, ct).ConfigureAwait(false);
                session.Review(actorId, review.AcknowledgeSelfReview, review.RecordedAtUtc);
                await _store.UpdateAsync(session, command.ExpectedVersion, actorId, command.Reason,
                    command.RecordedAtUtc, SessionReminderSync.None, null, ct).ConfigureAwait(false);
                break;
            case ScheduleSessionCommand:
                await RequireEligibleMaterialsAsync(session, session.Materials.Select(ToContractMaterial).ToArray(),
                    ToContractVisibility(session.Visibility), session.Language, ct).ConfigureAwait(false);
                session.Schedule(actorId, command.RecordedAtUtc);
                await _store.UpdateAsync(session, command.ExpectedVersion, actorId, command.Reason,
                    command.RecordedAtUtc, SessionReminderSync.Recompute,
                    session.ComputeReminderOccurrences(session.ScheduledAtUtc!.Value), ct).ConfigureAwait(false);
                break;
            case UpdateSessionCommand update:
                var materialSnapshot = update.Materials?.ToArray() ?? throw new ArgumentException("Materials are required.", nameof(update));
                if (session.Status == CommunitySessionStatus.Scheduled)
                    await RequireEligibleMaterialsAsync(session, materialSnapshot, update.Visibility, update.Language, ct).ConfigureAwait(false);
                session.UpdateDetails(
                    update.Title, update.Summary, update.AudienceDescription,
                    update.TopicTags, update.Presenter, update.Language,
                    update.StartUtc, update.DurationMinutes, update.Capacity,
                    materialSnapshot.Select(ToDomainMaterial).ToArray(),
                    ToDomainVisibility(update.Visibility), ToDomainChannel(update.ChannelType),
                    update.ChannelLink, update.ReminderLeadHours, command.RecordedAtUtc);
                var sync = session.Status == CommunitySessionStatus.Scheduled
                    ? SessionReminderSync.Recompute
                    : SessionReminderSync.None;
                var occurrences = sync == SessionReminderSync.Recompute
                    ? session.ComputeReminderOccurrences(session.ScheduledAtUtc!.Value)
                    : null;
                await _store.UpdateAsync(session, command.ExpectedVersion, actorId, command.Reason,
                    command.RecordedAtUtc, sync, occurrences, ct).ConfigureAwait(false);
                break;
            case CancelSessionCommand cancel:
                session.Cancel(cancel.CancellationReason, actorId, command.RecordedAtUtc);
                await _store.UpdateAsync(session, command.ExpectedVersion, actorId, command.Reason,
                    command.RecordedAtUtc, SessionReminderSync.SuppressPending, null, ct).ConfigureAwait(false);
                break;
            case CompleteSessionCommand:
                session.Complete(actorId, command.RecordedAtUtc);
                await _store.UpdateAsync(session, command.ExpectedVersion, actorId, command.Reason,
                    command.RecordedAtUtc, SessionReminderSync.None, null, ct).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), "An unsupported session command was supplied.");
        }

        return new SessionCommandResult(session.SessionId, session.Version, session.Status.ToString());
    }

    private async Task RequireEligibleMaterialsAsync(
        CommunitySession session, IReadOnlyList<Contracts.Sessions.SessionMaterialRef> materials,
        Contracts.Sessions.SessionVisibility visibility, string language, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var request = new SessionMaterialEligibilityRequest(session.CountryCode, session.RegionId, session.Sector,
            language, visibility, Array.AsReadOnly(materials.ToArray()));
        if (_materialEligibility is null || !await _materialEligibility.AreEligibleAsync(request, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Current approval and reuse rights for session materials could not be confirmed.");
    }

    private static Contracts.Sessions.SessionMaterialRef ToContractMaterial(DomainMaterial material) =>
        new(material.MaterialId, material.SourceVersion);

    public async Task<CommunitySessionView?> GetStaffAsync(Guid sessionId, string actorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new ArgumentException("A staff actor id is required.", nameof(actorId));
        }

        var session = await _store.LoadAsync(sessionId, ct).ConfigureAwait(false);
        return session is null ? null : ToStaffView(session);
    }

    public async Task<IReadOnlyList<PublicSessionView>> ListPublicAsync(int? limit, CancellationToken ct)
    {
        var take = limit ?? DefaultPublicLimit;
        if (take < 1 || take > MaxPublicLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), $"The public limit must be between 1 and {MaxPublicLimit}.");
        }

        var sessions = await _store.ListPublicAsync(take, ct).ConfigureAwait(false);
        return sessions
            .Where(IsListedPublicly)
            .OrderBy(item => item.StartUtc)
            .ThenBy(item => item.SessionId)
            .Take(take)
            .Select(ToPublicView)
            .ToList();
    }

    public async Task<PublicSessionPage> QueryPublicAsync(PublicSessionQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > MaxPublicLimit ||
            query.AfterStartUtc.HasValue != query.AfterSessionId.HasValue ||
            query.AfterSessionId == Guid.Empty ||
            query.StartFromUtc >= query.StartBeforeUtc ||
            query.LocalDateFrom > query.LocalDateTo ||
            new[] { query.Search, query.CountryCode, query.RegionId, query.Sector, query.Language, query.Topic, query.Category }
                .Any(value => value is { Length: > 120 }))
        {
            throw new ArgumentException("The public session query is invalid.", nameof(query));
        }

        var normalized = query with
        {
            Search = Normalize(query.Search),
            CountryCode = Normalize(query.CountryCode),
            RegionId = Normalize(query.RegionId),
            Sector = Normalize(query.Sector),
            Language = Normalize(query.Language),
            Topic = Normalize(query.Topic),
            Category = Normalize(query.Category),
            StartFromUtc = query.StartFromUtc?.ToUniversalTime(),
            StartBeforeUtc = query.StartBeforeUtc?.ToUniversalTime(),
            AfterStartUtc = query.AfterStartUtc?.ToUniversalTime()
        };

        var items = new List<PublicSessionView>(query.Limit);
        const int scanLimit = 1000;
        var scanned = 0;
        while (scanned < scanLimit)
        {
            var batchSize = Math.Min(101, scanLimit - scanned);
            var candidates = await _store.QueryPublicAsync(normalized, batchSize + 1, ct).ConfigureAwait(false);
            var hasMoreCandidates = candidates.Count > batchSize;
            CommunitySession? lastScanned = null;
            foreach (var candidate in candidates.Take(batchSize))
            {
                scanned++;
                lastScanned = candidate;
                if (!IsListedPublicly(candidate) || !MatchesLocalDates(candidate, query.LocalDateFrom, query.LocalDateTo))
                    continue;
                if (items.Count == query.Limit)
                {
                    var last = items[^1];
                    return new PublicSessionPage(items, new PublicSessionPosition(last.StartUtc, last.SessionId));
                }
                items.Add(ToPublicView(candidate));
            }

            if (!hasMoreCandidates || lastScanned is null)
                return new PublicSessionPage(items, null);
            if (scanned == scanLimit)
                return new PublicSessionPage(items, new PublicSessionPosition(lastScanned.StartUtc, lastScanned.SessionId));
            normalized = normalized with { AfterStartUtc = lastScanned.StartUtc, AfterSessionId = lastScanned.SessionId };
        }
        return new PublicSessionPage(items, null);
    }

    private static bool MatchesLocalDates(CommunitySession session, DateOnly? from, DateOnly? to)
    {
        if (from is null && to is null)
            return true;
        DateOnly date;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(session.TimeZoneId);
            date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(session.StartUtc, zone).DateTime);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            // A UTC calendar date is not a substitute for an unknown local date.
            return false;
        }
        return (from is null || date >= from) && (to is null || date <= to);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public async Task<PublicSessionView?> GetPublicAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await _store.LoadAsync(sessionId, ct).ConfigureAwait(false);
        return session is not null && IsPublic(session) && IsPublished(session)
            ? ToPublicView(session)
            : null;
    }

    public async Task<string?> GetIcsAsync(Guid sessionId, DateTimeOffset generatedAtUtc, CancellationToken ct)
    {
        var session = await _store.LoadAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || !IsPublic(session) || !IsPublished(session))
        {
            return null;
        }

        try
        {
            return CommunitySessionSharing.BuildIcs(session, generatedAtUtc);
        }
        catch (InvalidOperationException)
        {
            // Draft/Reviewed sessions never produce ICS.
            return null;
        }
    }

    public async Task<string?> GetInvitationTextAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await _store.LoadAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || !IsListedPublicly(session))
        {
            return null;
        }

        try
        {
            return CommunitySessionSharing.BuildInvitationText(session);
        }
        catch (InvalidOperationException)
        {
            // Invitations are manual copy-text for scheduled/cancelled sessions only.
            return null;
        }
    }

    private async Task<SessionCommandResult> ExecuteCreateAsync(
        CreateSessionDraftCommand command, string actorId, CancellationToken ct)
    {
        if (command.ExpectedVersion != 0)
        {
            var existing = await _store.LoadAsync(command.SessionId, ct).ConfigureAwait(false);
            throw new SessionVersionConflictException(
                command.SessionId, command.ExpectedVersion, existing?.Version ?? 0);
        }

        var present = await _store.LoadAsync(command.SessionId, ct).ConfigureAwait(false);
        if (present is not null)
        {
            throw new SessionVersionConflictException(command.SessionId, 0, present.Version);
        }

        var session = CommunitySession.Create(
            command.SessionId, command.Title, command.Summary, command.AudienceDescription,
            command.TopicTags, command.CountryCode, command.RegionId, command.Sector,
            command.Presenter, command.Language, command.StartUtc, command.TimeZoneId,
            command.DurationMinutes, command.Capacity,
            command.Materials.Select(ToDomainMaterial).ToArray(),
            ToDomainVisibility(command.Visibility), ToDomainChannel(command.ChannelType),
            command.ChannelLink, command.ReminderLeadHours, actorId, command.RecordedAtUtc);
        await _store.InsertAsync(session, actorId, command.Reason, command.RecordedAtUtc, ct).ConfigureAwait(false);
        return new SessionCommandResult(session.SessionId, session.Version, session.Status.ToString());
    }

    private static bool IsPublic(CommunitySession session) =>
        session.Visibility == DomainVisibility.Public;

    private static bool IsListedPublicly(CommunitySession session) =>
        IsPublic(session) && session.PublishedVersion > 0 && session.ScheduledAtUtc is not null &&
        (session.Status is CommunitySessionStatus.Scheduled or CommunitySessionStatus.Cancelled);

    private static bool IsPublished(CommunitySession session) =>
        session.PublishedVersion > 0 && session.ScheduledAtUtc is not null &&
        session.Status is CommunitySessionStatus.Scheduled
            or CommunitySessionStatus.Cancelled
            or CommunitySessionStatus.Completed;

    private static CommunitySessionView ToStaffView(CommunitySession session) => new(
        session.SessionId, session.Version, session.Title, session.Summary,
        session.AudienceDescription, session.TopicTags.ToArray(),
        session.CountryCode, session.RegionId, session.Sector, session.Presenter, session.Language,
        session.StartUtc, session.TimeZoneId, session.DurationMinutes, session.Capacity,
        session.Materials.Select(ToStoredMaterial).ToArray(),
        ToContractVisibility(session.Visibility), ToContractChannel(session.ChannelType),
        session.ChannelLink, session.ReminderLeadHours.ToArray(),
        session.Status.ToString(), session.CreatedByActorId, session.ReviewedByActorId,
        session.SelfReviewAcknowledged, session.IcsSequence, session.CancellationReason);

    private static PublicSessionView ToPublicView(CommunitySession session) => new(
        session.SessionId, session.Version, session.Title, session.Summary,
        session.AudienceDescription, session.TopicTags.ToArray(),
        session.CountryCode, session.RegionId, session.Sector, session.Presenter, session.Language,
        session.StartUtc, session.TimeZoneId, session.DurationMinutes, session.Capacity,
        // No authoritative public-rights and withdrawal lookup is wired here.
        // Preserve staff references, but do not serialize unverified references publicly.
        Array.Empty<StoredSessionMaterial>(),
        ToContractChannel(session.ChannelType), session.ChannelLink,
        session.Status.ToString(), session.CancellationReason, session.IcsSequence);

    private static DomainVisibility ToDomainVisibility(Contracts.Sessions.SessionVisibility visibility) =>
        visibility switch
        {
            Contracts.Sessions.SessionVisibility.Public => DomainVisibility.Public,
            Contracts.Sessions.SessionVisibility.Internal => DomainVisibility.Internal,
            _ => throw new ArgumentOutOfRangeException(nameof(visibility)),
        };

    private static DomainChannel ToDomainChannel(Contracts.Sessions.SessionChannelType channel) =>
        channel switch
        {
            Contracts.Sessions.SessionChannelType.None => DomainChannel.None,
            Contracts.Sessions.SessionChannelType.FacebookPage => DomainChannel.FacebookPage,
            Contracts.Sessions.SessionChannelType.WhatsAppGroup => DomainChannel.WhatsAppGroup,
            Contracts.Sessions.SessionChannelType.Other => DomainChannel.Other,
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };

    private static DomainMaterial ToDomainMaterial(Contracts.Sessions.SessionMaterialRef material) =>
        new(material.MaterialId, material.SourceVersion);

    private static Contracts.Sessions.SessionVisibility ToContractVisibility(DomainVisibility visibility) =>
        visibility switch
        {
            DomainVisibility.Public => Contracts.Sessions.SessionVisibility.Public,
            DomainVisibility.Internal => Contracts.Sessions.SessionVisibility.Internal,
            _ => throw new ArgumentOutOfRangeException(nameof(visibility)),
        };

    private static Contracts.Sessions.SessionChannelType ToContractChannel(DomainChannel channel) =>
        channel switch
        {
            DomainChannel.None => Contracts.Sessions.SessionChannelType.None,
            DomainChannel.FacebookPage => Contracts.Sessions.SessionChannelType.FacebookPage,
            DomainChannel.WhatsAppGroup => Contracts.Sessions.SessionChannelType.WhatsAppGroup,
            DomainChannel.Other => Contracts.Sessions.SessionChannelType.Other,
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };

    private static StoredSessionMaterial ToStoredMaterial(DomainMaterial material) =>
        new(material.MaterialId, material.SourceVersion);
}
