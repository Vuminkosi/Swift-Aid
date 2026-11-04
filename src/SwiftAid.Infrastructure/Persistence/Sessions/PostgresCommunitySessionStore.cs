using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Sessions;
using SwiftAid.Domain.Sessions;
using DomainSessionVisibility = SwiftAid.Domain.Sessions.SessionVisibility;
using PublicSessionQuery = SwiftAid.Contracts.Sessions.PublicSessionQuery;

namespace SwiftAid.Infrastructure.Persistence.Sessions;

/// <summary>
/// PostgreSQL-backed community-session store (P10.1). The domain aggregate has no
/// public rehydration factory and its source is owned by another lane, so persisted
/// rows are rehydrated through the compiler-generated auto-property backing fields.
/// Rehydration only restores already-validated state; every mutation goes through the
/// domain methods in <see cref="CommunitySessionService"/>.
/// </summary>
public sealed class PostgresCommunitySessionStore(SwiftAidDbContext dbContext) : ICommunitySessionStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<CommunitySession?> LoadAsync(Guid sessionId, CancellationToken ct)
    {
        var entity = await Entities().AsNoTracking()
            .SingleOrDefaultAsync(item => item.SessionId == sessionId, ct)
            .ConfigureAwait(false);
        return entity is null ? null : Rehydrate(entity);
    }

    public Task<string?> GetOrganizationIdAsync(Guid sessionId, CancellationToken ct) =>
        Entities().AsNoTracking().Where(item => item.SessionId == sessionId)
            .Select(item => item.OrganizationId).SingleOrDefaultAsync(ct);

    public async Task InsertAsync(
        CommunitySession session,
        string actorId,
        string reason,
        DateTimeOffset recordedAtUtc,
        SessionRevisionAction action,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        _dbContext.Set<CommunitySessionEntity>().Add(ToEntity(session));
        AppendRevision(session, actorId, reason, recordedAtUtc, action, null);
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(
        CommunitySession session,
        long expectedVersion,
        string actorId,
        string reason,
        DateTimeOffset recordedAtUtc,
        SessionReminderSync reminderSync,
        IReadOnlyList<SessionReminderOccurrence>? occurrences,
        SessionRevisionAction action,
        DateTimeOffset? previousStartUtc,
        CancellationToken ct,
        IReadOnlyList<SwiftAid.Contracts.Sessions.SessionMaterialApproval>? materialApprovals = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var entity = await Entities()
            .SingleOrDefaultAsync(item => item.SessionId == session.SessionId, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            throw new KeyNotFoundException($"Community session {session.SessionId:D} was not found.");
        }

        if (entity.Version != expectedVersion)
        {
            throw new SessionVersionConflictException(session.SessionId, expectedVersion, entity.Version);
        }

        OverwriteEntity(session, entity);
        if (materialApprovals is not null)
        {
            foreach (var approval in materialApprovals)
            {
                var current = await _dbContext.SessionMaterialApprovals.SingleOrDefaultAsync(item =>
                    item.OrganizationId == approval.OrganizationId && item.MaterialId == approval.MaterialId &&
                    item.SourceVersion == approval.SourceVersion, ct).ConfigureAwait(false);
                if (current is null)
                {
                    _dbContext.SessionMaterialApprovals.Add(new SessionMaterialApprovalEntity
                    {
                        OrganizationId = approval.OrganizationId, MaterialId = approval.MaterialId,
                        SourceVersion = approval.SourceVersion, RightsEvidence = approval.RightsEvidence,
                        ApprovedByActorId = approval.ApprovedByActorId, ApprovedAtUtc = approval.ApprovedAtUtc
                    });
                }
                else
                {
                    current.RightsEvidence = approval.RightsEvidence;
                    current.ApprovedByActorId = approval.ApprovedByActorId;
                    current.ApprovedAtUtc = approval.ApprovedAtUtc;
                    current.RevokedByActorId = null;
                    current.RevokedAtUtc = null;
                    current.Version++;
                }
            }
        }
        AppendRevision(session, actorId, reason, recordedAtUtc, action, previousStartUtc);
        await SyncRemindersAsync(session, reminderSync, occurrences ?? [], recordedAtUtc, ct).ConfigureAwait(false);

        try
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new SessionVersionConflictException(session.SessionId, expectedVersion, actualVersion: -1, exception);
        }
    }

    public async Task<IReadOnlyList<CommunitySession>> ListPublicAsync(int take, CancellationToken ct)
    {
        var entities = await Entities().AsNoTracking()
            .Where(item => item.Visibility == nameof(DomainSessionVisibility.Public) &&
                item.PublishedVersion > 0 && item.ScheduledAtUtc != null &&
                (item.Status == nameof(CommunitySessionStatus.Scheduled) ||
                 item.Status == nameof(CommunitySessionStatus.Cancelled) ||
                 item.Status == nameof(CommunitySessionStatus.Completed)))
            .OrderBy(item => item.StartUtc)
            .ThenBy(item => item.SessionId)
            .Take(Math.Max(1, take))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return entities.Select(Rehydrate).ToList();
    }

    public async Task<IReadOnlyList<CommunitySession>> QueryPublicAsync(PublicSessionQuery query, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rows = Entities().AsNoTracking()
            .Where(item => item.Visibility == nameof(DomainSessionVisibility.Public) &&
                item.PublishedVersion > 0 && item.ScheduledAtUtc != null &&
                (item.Status == nameof(CommunitySessionStatus.Scheduled) ||
                 item.Status == nameof(CommunitySessionStatus.Cancelled) ||
                 item.Status == nameof(CommunitySessionStatus.Completed)));

        if (query.Search is { } search)
        {
            var term = search.ToLowerInvariant();
            var exactTopic = JsonSerializer.Serialize(new[] { search });
            rows = rows.Where(item => item.Title.ToLower().Contains(term) ||
                item.Summary.ToLower().Contains(term) || item.Presenter.ToLower().Contains(term) ||
                item.CountryCode.ToLower().Contains(term) || item.RegionId.ToLower().Contains(term) ||
                item.Language.ToLower().Contains(term) || EF.Functions.JsonContains(item.TopicTagsJson, exactTopic));
        }

        if (query.CountryCode is { } country)
            rows = rows.Where(item => item.CountryCode.ToLower() == country.ToLower());
        if (query.RegionId is { } region)
            rows = rows.Where(item => item.RegionId.ToLower() == region.ToLower());
        if (query.Sector is { } sector)
            rows = rows.Where(item => item.Sector.ToLower() == sector.ToLower());
        if (query.Language is { } language)
            rows = rows.Where(item => item.Language.ToLower() == language.ToLower());
        if (query.Category is { } category)
        {
            var exactCategory = JsonSerializer.Serialize(new[] { category });
            rows = rows.Where(item => item.Sector.ToLower() == category.ToLower() ||
                EF.Functions.JsonContains(item.TopicTagsJson, exactCategory));
        }
        if (query.Topic is { } topic)
        {
            var exactTopic = JsonSerializer.Serialize(new[] { topic });
            rows = rows.Where(item => EF.Functions.JsonContains(item.TopicTagsJson, exactTopic));
        }
        if (query.Status is { } status)
            rows = rows.Where(item => item.Status == status);
        if (query.StartFromUtc is { } from)
            rows = rows.Where(item => item.StartUtc >= from);
        if (query.StartBeforeUtc is { } before)
            rows = rows.Where(item => item.StartUtc < before);
        // Every TimeZoneInfo UTC offset is within +/-14 hours. These broad UTC
        // bounds reduce scanning; the application applies the exact local date.
        if (query.LocalDateFrom is { } localFrom && localFrom > DateOnly.MinValue)
        {
            var earliest = new DateTimeOffset(localFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).AddHours(-14);
            rows = rows.Where(item => item.StartUtc >= earliest);
        }
        if (query.LocalDateTo is { } localTo && localTo < DateOnly.MaxValue.AddDays(-1))
        {
            var latest = new DateTimeOffset(localTo.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).AddHours(14);
            rows = rows.Where(item => item.StartUtc < latest);
        }
        if (query.AfterStartUtc is { } after && query.AfterSessionId is { } afterId)
            rows = rows.Where(item => item.StartUtc > after ||
                item.StartUtc == after && item.SessionId.CompareTo(afterId) > 0);

        var entities = await rows.OrderBy(item => item.StartUtc).ThenBy(item => item.SessionId)
            .Take(take).ToListAsync(ct).ConfigureAwait(false);
        return entities.Select(Rehydrate).ToList();
    }

    public Task<int> CountRevisionsAsync(Guid sessionId, CancellationToken ct) =>
        _dbContext.Set<CommunitySessionRevisionEntity>().CountAsync(item => item.SessionId == sessionId, ct);

    public async Task<IReadOnlyList<SwiftAid.Contracts.Sessions.SessionHistoryEntry>> ReadHistoryAsync(
        Guid sessionId, long? beforeVersion, int take, CancellationToken ct)
    {
        if (sessionId == Guid.Empty || beforeVersion is <= 0 || take is < 1 or > 101)
            throw new ArgumentException("A valid session, cursor and page size are required.");
        var query = _dbContext.Set<CommunitySessionRevisionEntity>().AsNoTracking()
            .Where(item => item.SessionId == sessionId);
        if (beforeVersion is { } before) query = query.Where(item => item.Version < before);
        var rows = await query.OrderByDescending(item => item.Version).Take(take).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToHistoryEntry).ToArray();
    }

    private static SwiftAid.Contracts.Sessions.SessionHistoryEntry ToHistoryEntry(CommunitySessionRevisionEntity row)
    {
        using var snapshot = JsonDocument.Parse(row.SnapshotJson);
        var root = snapshot.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new(row.Version, row.ActorId, row.Reason, row.RecordedAtUtc, null, null, null, null);
        var action = root.TryGetProperty("Action", out var actionValue) && actionValue.ValueKind == JsonValueKind.String &&
            Enum.TryParse<SessionRevisionAction>(actionValue.GetString(), out var parsedAction) &&
            string.Equals(parsedAction.ToString(), actionValue.GetString(), StringComparison.Ordinal) && Enum.IsDefined(parsedAction)
                ? parsedAction.ToString() : null;
        var status = root.TryGetProperty("Status", out var statusValue) && statusValue.ValueKind == JsonValueKind.String &&
            Enum.TryParse<CommunitySessionStatus>(statusValue.GetString(), out var parsedStatus) &&
            string.Equals(parsedStatus.ToString(), statusValue.GetString(), StringComparison.Ordinal) && Enum.IsDefined(parsedStatus)
                ? parsedStatus.ToString() : null;
        static DateTimeOffset? ReadDate(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var date)
                ? date : null;
        return new(row.Version, row.ActorId, row.Reason, row.RecordedAtUtc, action, status,
            ReadDate(root, "StartUtc"), ReadDate(root, "PreviousStartUtc"));
    }

    public Task<int> CountPendingRemindersAsync(Guid sessionId, CancellationToken ct) =>
        _dbContext.Set<SessionReminderOccurrenceEntity>()
            .CountAsync(item => item.SessionId == sessionId && item.Status == "Pending", ct);

    public Task<int> CountSuppressedRemindersAsync(Guid sessionId, CancellationToken ct) =>
        _dbContext.Set<SessionReminderOccurrenceEntity>()
            .CountAsync(item => item.SessionId == sessionId && item.Status == "Suppressed", ct);

    private DbSet<CommunitySessionEntity> Entities() => _dbContext.Set<CommunitySessionEntity>();

    private void AppendRevision(
        CommunitySession session,
        string actorId,
        string reason,
        DateTimeOffset recordedAtUtc,
        SessionRevisionAction action,
        DateTimeOffset? previousStartUtc)
    {
        _dbContext.Set<CommunitySessionRevisionEntity>().Add(new CommunitySessionRevisionEntity
        {
            RevisionId = Guid.NewGuid(),
            SessionId = session.SessionId,
            Version = session.Version,
            ActorId = actorId,
            Reason = reason,
            RecordedAtUtc = recordedAtUtc,
            SnapshotJson = BuildSnapshot(session, action, previousStartUtc),
        });
    }

    private async Task SyncRemindersAsync(
        CommunitySession session,
        SessionReminderSync sync,
        IReadOnlyList<SessionReminderOccurrence> occurrences,
        DateTimeOffset recordedAtUtc,
        CancellationToken ct)
    {
        var reminders = _dbContext.Set<SessionReminderOccurrenceEntity>();
        switch (sync)
        {
            case SessionReminderSync.None:
                return;
            case SessionReminderSync.Recompute:
                // Reminder dispatch/execution belongs to the P6.3 durable-reminder lane.
                // Only non-sent rows are replaced; Sent rows are left untouched.
                await _dbContext.SessionReminderDeliveries
                    .Where(item => item.SessionId == session.SessionId && item.Status == "Pending")
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(item => item.Status, "Suppressed")
                        .SetProperty(item => item.LeaseId, (Guid?)null)
                        .SetProperty(item => item.LeaseUntilUtc, (DateTimeOffset?)null)
                        .SetProperty(item => item.UpdatedAtUtc, recordedAtUtc), ct)
                    .ConfigureAwait(false);
                await _dbContext.SessionReminderDeliveries
                    .Where(item => item.SessionId == session.SessionId && item.Status == "InProgress")
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(item => item.Status, "OutcomeUnknown")
                        .SetProperty(item => item.LeaseId, (Guid?)null)
                        .SetProperty(item => item.LeaseUntilUtc, (DateTimeOffset?)null)
                        .SetProperty(item => item.UpdatedAtUtc, recordedAtUtc), ct)
                    .ConfigureAwait(false);
                var stale = await reminders
                    .Where(item => item.SessionId == session.SessionId && item.Status != "Sent")
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                _dbContext.RemoveRange(stale);
                foreach (var occurrence in occurrences)
                {
                    reminders.Add(new SessionReminderOccurrenceEntity
                    {
                        OccurrenceId = Guid.NewGuid(),
                        SessionId = session.SessionId,
                        RemindAtUtc = occurrence.RemindAtUtc,
                        LeadHours = occurrence.LeadHours,
                        Status = "Pending",
                        Version = session.Version,
                    });
                }

                return;
            case SessionReminderSync.SuppressPending:
                await _dbContext.SessionReminderDeliveries
                    .Where(item => item.SessionId == session.SessionId && item.Status == "Pending")
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(item => item.Status, "Suppressed")
                        .SetProperty(item => item.LeaseId, (Guid?)null)
                        .SetProperty(item => item.LeaseUntilUtc, (DateTimeOffset?)null)
                        .SetProperty(item => item.UpdatedAtUtc, recordedAtUtc), ct)
                    .ConfigureAwait(false);
                await _dbContext.SessionReminderDeliveries
                    .Where(item => item.SessionId == session.SessionId && item.Status == "InProgress")
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(item => item.Status, "OutcomeUnknown")
                        .SetProperty(item => item.LeaseId, (Guid?)null)
                        .SetProperty(item => item.LeaseUntilUtc, (DateTimeOffset?)null)
                        .SetProperty(item => item.UpdatedAtUtc, recordedAtUtc), ct)
                    .ConfigureAwait(false);
                await reminders
                    .Where(item => item.SessionId == session.SessionId && item.Status == "Pending")
                    .ExecuteUpdateAsync(
                        update => update.SetProperty(item => item.Status, "Suppressed"), ct)
                    .ConfigureAwait(false);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(sync));
        }
    }

    private static CommunitySessionEntity ToEntity(CommunitySession session)
    {
        var entity = new CommunitySessionEntity { SessionId = session.SessionId };
        OverwriteEntity(session, entity);
        return entity;
    }

    private static void OverwriteEntity(CommunitySession session, CommunitySessionEntity entity)
    {
        entity.OrganizationId = session.OrganizationId;
        entity.Version = session.Version;
        entity.Title = session.Title;
        entity.Summary = session.Summary;
        entity.AudienceDescription = session.AudienceDescription;
        entity.TopicTagsJson = JsonSerializer.Serialize(session.TopicTags);
        entity.CountryCode = session.CountryCode;
        entity.RegionId = session.RegionId;
        entity.Sector = session.Sector;
        entity.Presenter = session.Presenter;
        entity.Language = session.Language;
        entity.StartUtc = session.StartUtc;
        entity.TimeZoneId = session.TimeZoneId;
        entity.DurationMinutes = session.DurationMinutes;
        entity.Capacity = session.Capacity;
        entity.MaterialsJson = JsonSerializer.Serialize(
            session.Materials.Select(material => new MaterialJson(material.MaterialId, material.SourceVersion, material.RightsEvidence)));
        entity.Visibility = session.Visibility.ToString();
        entity.ChannelType = session.ChannelType.ToString();
        entity.ChannelLink = session.ChannelLink;
        entity.ReminderLeadHoursJson = JsonSerializer.Serialize(session.ReminderLeadHours);
        entity.Status = session.Status.ToString();
        entity.CreatedByActorId = session.CreatedByActorId;
        entity.CreatedAtUtc = session.CreatedAtUtc;
        entity.ReviewedByActorId = session.ReviewedByActorId;
        entity.ReviewedAtUtc = session.ReviewedAtUtc;
        entity.SelfReviewAcknowledged = session.SelfReviewAcknowledged;
        entity.ScheduledAtUtc = session.ScheduledAtUtc;
        entity.PublishedVersion = session.PublishedVersion;
        entity.CancellationReason = session.CancellationReason;
        entity.CancelledAtUtc = session.CancelledAtUtc;
        entity.CompletedAtUtc = session.CompletedAtUtc;
    }

    private static CommunitySession Rehydrate(CommunitySessionEntity entity)
    {
        var session = (CommunitySession)RuntimeHelpers.GetUninitializedObject(typeof(CommunitySession));
        SetState(session, nameof(CommunitySession.SessionId), entity.SessionId);
        SetState(session, nameof(CommunitySession.OrganizationId), entity.OrganizationId ?? string.Empty);
        SetState(session, nameof(CommunitySession.Version), entity.Version);
        SetState(session, nameof(CommunitySession.Title), entity.Title);
        SetState(session, nameof(CommunitySession.Summary), entity.Summary);
        SetState(session, nameof(CommunitySession.AudienceDescription), entity.AudienceDescription);
        SetState(session, nameof(CommunitySession.TopicTags),
            (IReadOnlyList<string>)(JsonSerializer.Deserialize<List<string>>(entity.TopicTagsJson) ?? []));
        SetState(session, nameof(CommunitySession.CountryCode), entity.CountryCode);
        SetState(session, nameof(CommunitySession.RegionId), entity.RegionId);
        SetState(session, nameof(CommunitySession.Sector), entity.Sector);
        SetState(session, nameof(CommunitySession.Presenter), entity.Presenter);
        SetState(session, nameof(CommunitySession.Language), entity.Language);
        SetState(session, nameof(CommunitySession.StartUtc), entity.StartUtc);
        SetState(session, nameof(CommunitySession.TimeZoneId), entity.TimeZoneId);
        SetState(session, nameof(CommunitySession.DurationMinutes), entity.DurationMinutes);
        SetState(session, nameof(CommunitySession.Capacity), entity.Capacity);
        SetState(session, nameof(CommunitySession.Materials),
            (IReadOnlyList<SessionMaterialRef>)(JsonSerializer.Deserialize<List<MaterialJson>>(entity.MaterialsJson) ?? [])
                .Select(material => new SessionMaterialRef(material.MaterialId, material.SourceVersion, material.RightsEvidence))
                .ToArray());
        SetState(session, nameof(CommunitySession.Visibility),
            Enum.Parse<SessionVisibility>(entity.Visibility));
        SetState(session, nameof(CommunitySession.ChannelType),
            Enum.Parse<SessionChannelType>(entity.ChannelType));
        SetState(session, nameof(CommunitySession.ChannelLink), entity.ChannelLink);
        SetState(session, nameof(CommunitySession.ReminderLeadHours),
            (IReadOnlyList<int>)(JsonSerializer.Deserialize<List<int>>(entity.ReminderLeadHoursJson) ?? []));
        SetState(session, nameof(CommunitySession.Status),
            Enum.Parse<CommunitySessionStatus>(entity.Status));
        SetState(session, nameof(CommunitySession.CreatedByActorId), entity.CreatedByActorId);
        SetState(session, nameof(CommunitySession.CreatedAtUtc), entity.CreatedAtUtc);
        SetState(session, nameof(CommunitySession.ReviewedByActorId), entity.ReviewedByActorId);
        SetState(session, nameof(CommunitySession.ReviewedAtUtc), entity.ReviewedAtUtc);
        SetState(session, nameof(CommunitySession.SelfReviewAcknowledged), entity.SelfReviewAcknowledged);
        SetState(session, nameof(CommunitySession.ScheduledAtUtc), entity.ScheduledAtUtc);
        SetState(session, nameof(CommunitySession.PublishedVersion), entity.PublishedVersion);
        SetState(session, nameof(CommunitySession.CancellationReason), entity.CancellationReason);
        SetState(session, nameof(CommunitySession.CancelledAtUtc), entity.CancelledAtUtc);
        SetState(session, nameof(CommunitySession.CompletedAtUtc), entity.CompletedAtUtc);
        return session;
    }

    private static void SetState<T>(CommunitySession session, string name, T? value)
    {
        var field = typeof(CommunitySession).GetField(
            $"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
        {
            throw new InvalidOperationException(
                $"CommunitySession backing field for '{name}' was not found; the domain shape changed.");
        }

        field.SetValue(session, value);
    }

    private static string BuildSnapshot(CommunitySession session, SessionRevisionAction action, DateTimeOffset? previousStartUtc) =>
        JsonSerializer.Serialize(new
        {
            Action = action.ToString(),
            PreviousStartUtc = previousStartUtc,
            session.SessionId,
            session.Version,
            Status = session.Status.ToString(),
            session.Title,
            session.Summary,
            session.AudienceDescription,
            session.TopicTags,
            session.CountryCode,
            session.RegionId,
            session.Sector,
            session.Presenter,
            session.Language,
            session.StartUtc,
            session.TimeZoneId,
            session.DurationMinutes,
            session.Capacity,
            Materials = session.Materials.Select(material => new { material.MaterialId, material.SourceVersion }),
            Visibility = session.Visibility.ToString(),
            ChannelType = session.ChannelType.ToString(),
            session.ChannelLink,
            session.ReminderLeadHours,
            session.CreatedByActorId,
            session.CreatedAtUtc,
            session.ReviewedByActorId,
            session.ReviewedAtUtc,
            session.SelfReviewAcknowledged,
            session.ScheduledAtUtc,
            session.PublishedVersion,
            session.CancellationReason,
            session.CancelledAtUtc,
            session.CompletedAtUtc,
        });

    private sealed record MaterialJson(string MaterialId, string SourceVersion, string? RightsEvidence = null);
}
