using Microsoft.Extensions.Options;
using SwiftAid.Application.HealthEvidence;

namespace SwiftAid.Infrastructure.HealthEvidence;

/// <summary>Explicit approved source metadata. No source content is fetched or generated.</summary>
public sealed class ApprovedHealthSourceOptions
{
    public string[] AllowedHosts { get; set; } = [];
    public ApprovedHealthSourceEntry[] Sources { get; set; } = [];
}

public sealed class ApprovedHealthSourceEntry
{
    public bool Approved { get; set; }
    public string Id { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Title { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Jurisdiction { get; set; } = "";
    public string Language { get; set; } = "";
    public string Version { get; set; } = "";
    public string License { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset ReviewedAtUtc { get; set; }
    public DateTimeOffset ValidUntilUtc { get; set; }
}

/// <summary>Read-only registry; malformed entries are excluded rather than treated as approved evidence.</summary>
public sealed class ConfiguredHealthSourceRegistry(IOptions<ApprovedHealthSourceOptions> options, TimeProvider clock)
    : IApprovedHealthSourceRegistry
{
    public Task<HealthSourceSearchResult> SearchAsync(string topic, string jurisdiction, string language, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Valid(topic, 100) || !Valid(jurisdiction, 40) || !Valid(language, 35))
            throw new ArgumentException("A bounded topic, jurisdiction and language are required.");
        var settings = options.Value;
        if (settings.AllowedHosts is null || settings.Sources is null || settings.AllowedHosts.Length is < 1 or > 100 || settings.Sources.Length > 1000)
            return Task.FromResult(new HealthSourceSearchResult([], false));
        var hosts = settings.AllowedHosts.Where(host => Valid(host, 253) && Uri.CheckHostName(host) == UriHostNameType.Dns &&
            host == host.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        if (hosts.Count == 0) return Task.FromResult(new HealthSourceSearchResult([], false));
        var duplicateIds = settings.Sources.Where(source => source is not null && source.Approved && Valid(source.Id, 100))
            .GroupBy(source => source.Id, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var now = clock.GetUtcNow();
        var matches = new List<ApprovedHealthSource>();
        foreach (var source in settings.Sources)
        {
            if (source is null || !source.Approved || !Valid(source.Id, 100) || duplicateIds.Contains(source.Id) || !Valid(source.Topic, 100) ||
                !Valid(source.Title, 200) || !Valid(source.Publisher, 150) || !Valid(source.Jurisdiction, 40) ||
                !Valid(source.Language, 35) || !Valid(source.Version, 100) || !Valid(source.License, 150) ||
                source.ReviewedAtUtc.Offset != TimeSpan.Zero || source.ValidUntilUtc.Offset != TimeSpan.Zero ||
                source.ReviewedAtUtc == default || source.ReviewedAtUtc > now || source.ValidUntilUtc <= now ||
                source.ValidUntilUtc <= source.ReviewedAtUtc || !Valid(source.Url, 2048) || !Uri.TryCreate(source.Url, UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) ||
                !string.IsNullOrEmpty(url.Fragment) || !hosts.Contains(url.IdnHost.ToLowerInvariant()) ||
                !string.Equals(source.Topic, topic, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(source.Jurisdiction, jurisdiction, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(source.Language, language, StringComparison.OrdinalIgnoreCase)) continue;
            matches.Add(new(source.Id, source.Topic, source.Title, source.Publisher, source.Jurisdiction,
                source.Language, source.Version, source.License, url, source.ReviewedAtUtc, source.ValidUntilUtc));
        }
        // Ambiguous identities cannot provide stable provenance.
        var unique = matches.OrderBy(source => source.Id, StringComparer.Ordinal).Take(20).ToArray();
        return Task.FromResult(new HealthSourceSearchResult(unique, true, matches.Count > unique.Length));
    }

    private static bool Valid(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit &&
        value == value.Trim() && !value.Any(char.IsControl);
}
