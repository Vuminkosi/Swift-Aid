using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Cases;

public sealed record HealthOfficerDesignationChange(Guid CommandId, string TargetActorId, string OrganizationId,
    string CountryCode, string Sector, string Status, DateTimeOffset ValidFromUtc, DateTimeOffset ExpiresAtUtc, long ExpectedVersion);

public enum HealthOfficerDesignationChangeStatus { Recorded, Replayed, Conflict, Denied, Invalid, Unavailable }

public sealed record HealthOfficerDesignationChangeResult(HealthOfficerDesignationChangeStatus Status, Guid? DesignationId = null, long? Version = null);

/// <summary>Changes a scoped role designation under explicit administrator authority, never licensure.</summary>
public interface IHealthOfficerDesignationAdministration
{
    Task<HealthOfficerDesignationChangeResult> ChangeAsync(ActorContext actor, HealthOfficerDesignationChange change, CancellationToken ct);
}

public sealed record HealthOfficerDesignationScope(string TargetActorId, string OrganizationId, string CountryCode, string Sector);
public sealed record HealthOfficerDesignationCurrent(Guid DesignationId, long Version, string Status, DateTimeOffset ValidFromUtc, DateTimeOffset ExpiresAtUtc);
public enum HealthOfficerDesignationReadStatus { Available, Denied, Invalid, Unavailable }
public sealed record HealthOfficerDesignationReadResult(HealthOfficerDesignationReadStatus Status, HealthOfficerDesignationCurrent? Current = null);

public interface IHealthOfficerDesignationQuery
{
    Task<HealthOfficerDesignationReadResult> GetAsync(ActorContext actor, HealthOfficerDesignationScope scope, CancellationToken ct);
}
