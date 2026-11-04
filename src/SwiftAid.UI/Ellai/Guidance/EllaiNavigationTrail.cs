using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>Bounded in-memory application locations, never browser history or model-supplied URLs.</summary>
public sealed class EllaiNavigationTrail
{
    private readonly List<string> _routes = [];
    public string? Previous => _routes.Count > 1 ? _routes[^2] : null;
    public static string? CanonicalRoute(string relativePath)
    {
        var route = "/" + relativePath.Split('?', '#')[0].Trim('/');
        if (EllaiNavigationManifest.Destinations.Any(item => item.Route == route)) return route;
        var parts = route.Split('/');
        return parts.Length == 3 && parts[1] == "case" && parts[2].Length == 36 &&
            Guid.TryParseExact(parts[2], "D", out var id) && id != Guid.Empty ? $"/case/{id:D}" : null;
    }
    public void Record(string relativePath)
    {
        var route = CanonicalRoute(relativePath);
        if (route is null) { _routes.Clear(); return; }
        if (_routes.LastOrDefault() == route) return;
        if (Previous == route) { _routes.RemoveAt(_routes.Count - 1); return; }
        _routes.Add(route);
        if (_routes.Count > 20) _routes.RemoveAt(0);
    }
}
