using System.Text.Json.Serialization;

namespace SwiftAid.Contracts.Cases;

/// <summary>The authenticated principal and administrator grant are determined by the server.</summary>
public sealed class HealthOfficerDesignationChangeRequest
{
    [JsonRequired] public Guid CommandId { get; init; }
    [JsonRequired] public string TargetActorId { get; init; } = "";
    [JsonRequired] public string OrganizationId { get; init; } = "";
    [JsonRequired] public string CountryCode { get; init; } = "";
    [JsonRequired] public string Sector { get; init; } = "";
    [JsonRequired] public string Status { get; init; } = "";
    [JsonRequired] public DateTimeOffset ValidFromUtc { get; init; }
    [JsonRequired] public DateTimeOffset ExpiresAtUtc { get; init; }
    [JsonRequired] public long ExpectedVersion { get; init; }
}

public sealed record HealthOfficerDesignationChangeReceipt(Guid DesignationId, long Version, bool Replayed);

public sealed class HealthOfficerDesignationLookupRequest
{
    [JsonRequired] public string TargetActorId { get; init; } = "";
    [JsonRequired] public string OrganizationId { get; init; } = "";
    [JsonRequired] public string CountryCode { get; init; } = "";
    [JsonRequired] public string Sector { get; init; } = "";
}

public sealed record HealthOfficerDesignationView(Guid DesignationId, long Version, string Status, DateTimeOffset ValidFromUtc, DateTimeOffset ExpiresAtUtc);
public sealed record HealthOfficerDesignationLookupResponse(HealthOfficerDesignationView? Current);
