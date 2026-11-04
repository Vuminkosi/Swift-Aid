namespace SwiftAid.Application.HealthEvidence;

/// <summary>Server-configured reference metadata; approval does not authorize clinical advice or publication.</summary>
public sealed record ApprovedHealthSource(string Id, string Topic, string Title, string Publisher, string Jurisdiction,
    string Language, string Version, string License, Uri Url, DateTimeOffset ReviewedAtUtc, DateTimeOffset ValidUntilUtc);

public sealed record HealthSourceSearchResult(IReadOnlyList<ApprovedHealthSource> Sources, bool IsConfigured, bool Truncated = false);

public interface IApprovedHealthSourceRegistry
{
    Task<HealthSourceSearchResult> SearchAsync(string topic, string jurisdiction, string language, CancellationToken ct);
}
