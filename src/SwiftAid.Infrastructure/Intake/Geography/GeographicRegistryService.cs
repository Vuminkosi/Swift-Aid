using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Geography;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Intake.Geography;

/// <summary>Server-side registry lookup with seeding on first use. Search never returns the whole registry.</summary>
public sealed class GeographicRegistryService(
    SwiftAidDbContext dbContext,
    IGeographicRegistryImporter importer,
    ILogger<GeographicRegistryService> logger) : IGeographicRegistry
{
    public const int MaximumResults = 25;

    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private bool _seeded;

    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IGeographicRegistryImporter _importer = importer ?? throw new ArgumentNullException(nameof(importer));
    private readonly ILogger<GeographicRegistryService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<GeoSearchResponse> SearchAsync(
        string query,
        string? countryCode,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        await EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
        return await SearchCoreAsync(query, countryCode, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Searches an existing registry without starting a seed import; null means it is empty.</summary>
    public async Task<GeoSearchResponse?> SearchIfInitializedAsync(
        string query, string? countryCode, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (!await _dbContext.GeographicLocations.AsNoTracking().AnyAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return await SearchCoreAsync(query, countryCode, limit, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GeoSearchResponse> SearchCoreAsync(
        string query, string? countryCode, int limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit <= 0 ? MaximumResults : limit, 1, MaximumResults);
        var normalized = query.Trim();
        var country = string.IsNullOrWhiteSpace(countryCode) ? null : countryCode.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(StripWildcards(normalized)))
        {
            return new GeoSearchResponse(GeographicRegistryImporter.RegistryVersion, 0, []);
        }

        var pattern = $"%{StripWildcards(normalized)}%";
        var total = await _dbContext.GeographicLocations
            .AsNoTracking()
            .Where(item => item.IsActive)
            .Where(item => country == null || item.CountryCode == country)
            .Where(item => EF.Functions.Like(item.Name, pattern))
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);

        var matches = await _dbContext.GeographicLocations
            .AsNoTracking()
            .Where(item => item.IsActive)
            .Where(item => country == null || item.CountryCode == country)
            .Where(item => EF.Functions.Like(item.Name, pattern))
            .OrderBy(item => item.Name)
            .Take(Math.Max(take * 4, take))
            .ToDictionaryAsync(item => item.LocationId, item => item, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        var ranked = matches.Values
            .Select(item => (Item: item, Rank: Rank(item.Name, normalized)))
            .Where(entry => entry.Rank < int.MaxValue)
            .OrderBy(entry => entry.Rank)
            .ThenBy(entry => entry.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(entry => entry.Item)
            .ToArray();

        var results = new List<GeoSearchResult>(ranked.Length);
        foreach (var item in ranked)
        {
            var kind = GeographicRegistryMapper.TryParseKind(item.LocationKind, out var parsed)
                ? parsed
                : GeoLocationKind.Unknown;
            var ancestors = await GetAncestorsAsync(item, matches, cancellationToken).ConfigureAwait(false);
            var names = new[] { item.Name }.Concat(ancestors.Reverse().Select(ancestor => ancestor.Name)).ToArray();
            var countryName = ancestors.FirstOrDefault(ancestor => ancestor.Kind == GeoLocationKind.Country)?.Name
                ?? item.CountryCode;
            var hasChildren = await _dbContext.GeographicLocations
                .AsNoTracking()
                .AnyAsync(child => child.IsActive && child.ParentId == item.LocationId, cancellationToken)
                .ConfigureAwait(false);
            results.Add(new GeoSearchResult(
                item.LocationId,
                item.Name,
                kind,
                item.CountryCode,
                countryName,
                string.Join(", ", names),
                item.Latitude,
                item.Longitude,
                hasChildren,
                ancestors.Select(ancestor => new GeoAdminAncestor(ancestor.Id, ancestor.Name, ancestor.Kind)).ToArray()));
        }

        return new GeoSearchResponse(GeographicRegistryImporter.RegistryVersion, total, results);
    }

    public async Task<GeoChildrenResponse> GetChildrenAsync(string parentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentId);
        await EnsureSeededAsync(cancellationToken).ConfigureAwait(false);

        var parent = await _dbContext.GeographicLocations
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.LocationId == parentId && item.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (parent is null)
        {
            return new GeoChildrenResponse(GeographicRegistryImporter.RegistryVersion, parentId, []);
        }

        var children = await _dbContext.GeographicLocations
            .AsNoTracking()
            .Where(item => item.IsActive && item.ParentId == parentId)
            .OrderBy(item => item.Name)
            .Take(MaximumResults)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var parentAncestors = await GetAncestorsAsync(
            parent,
            new Dictionary<string, GeographicLocationEntity>(StringComparer.Ordinal),
            cancellationToken).ConfigureAwait(false);
        var parentKind = GeographicRegistryMapper.TryParseKind(parent.LocationKind, out var parsedParentKind)
            ? parsedParentKind
            : GeoLocationKind.Unknown;
        var parentChain = parentAncestors
            .Select(ancestor => new GeoAdminAncestor(ancestor.Id, ancestor.Name, ancestor.Kind))
            .Append(new GeoAdminAncestor(parent.LocationId, parent.Name, parentKind))
            .ToArray();

        var results = new List<GeoSearchResult>(children.Length);
        foreach (var child in children)
        {
            var kind = GeographicRegistryMapper.TryParseKind(child.LocationKind, out var parsed)
                ? parsed
                : GeoLocationKind.Unknown;
            results.Add(new GeoSearchResult(
                child.LocationId,
                child.Name,
                kind,
                child.CountryCode,
                parentChain.FirstOrDefault(ancestor => ancestor.Kind == GeoLocationKind.Country)?.Name
                    ?? child.CountryCode,
                $"{child.Name}, {parent.Name}",
                child.Latitude,
                child.Longitude,
                false,
                parentChain));
        }

        return new GeoChildrenResponse(GeographicRegistryImporter.RegistryVersion, parentId, results);
    }

    private async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        if (_seeded)
        {
            return;
        }

        await SeedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_seeded)
            {
                return;
            }

            if (!await _dbContext.GeographicLocations.AnyAsync(cancellationToken).ConfigureAwait(false))
            {
                await _importer.ImportAsync(cancellationToken).ConfigureAwait(false);
            }

            _seeded = true;
        }
        finally
        {
            SeedGate.Release();
        }
    }

    private sealed record ResolvedAncestor(string Id, string Name, GeoLocationKind Kind);

    /// <summary>Administrative chain above the item, root first (country before region).</summary>
    private async Task<IReadOnlyList<ResolvedAncestor>> GetAncestorsAsync(
        GeographicLocationEntity item,
        IReadOnlyDictionary<string, GeographicLocationEntity> preloaded,
        CancellationToken cancellationToken)
    {
        var chain = new List<ResolvedAncestor>();
        var current = item;
        for (var depth = 0; depth < 4; depth++)
        {
            if (string.IsNullOrWhiteSpace(current.ParentId))
            {
                break;
            }

            GeographicLocationEntity? parent = null;
            if (preloaded.TryGetValue(current.ParentId, out var cached))
            {
                parent = cached;
            }
            else
            {
                parent = await _dbContext.GeographicLocations
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.LocationId == current.ParentId && p.IsActive, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (parent is null)
            {
                break;
            }

            var kind = GeographicRegistryMapper.TryParseKind(parent.LocationKind, out var parentKind)
                ? parentKind
                : GeoLocationKind.Unknown;
            chain.Add(new ResolvedAncestor(parent.LocationId, parent.Name, kind));
            current = parent;
        }

        chain.Reverse();
        return chain;
    }

    private static int Rank(string name, string query) =>
        string.Equals(name, query, StringComparison.OrdinalIgnoreCase) ? 0 :
        name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 :
        name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : int.MaxValue;

    private static string StripWildcards(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '%' or '_' or '[' or ']' or '\\')
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
