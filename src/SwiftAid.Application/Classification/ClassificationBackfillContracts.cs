using SwiftAid.Contracts.Events;

namespace SwiftAid.Application.Classification;

public sealed record StartClassificationBackfill(Guid JobId, string WorkspaceId, string OrganizationId);
public sealed record ClassificationBackfillControl(bool Paused);
public sealed record ClassificationBackfillStatus(Guid JobId, string State, string ConfigurationHash,
    long Eligible, long Pending, long NewlyClassified, long Reused, long Abstained, long Failed,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, long Superseded = 0, long Expired = 0);
public sealed record ClassificationBackfillWork(ClassificationBackfillStatus Status, IngestionNotice? Notice);
