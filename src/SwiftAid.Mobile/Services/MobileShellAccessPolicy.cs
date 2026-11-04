namespace SwiftAid.Mobile.Services;

/// <summary>Public shell routes and launcher visibility; protected services still authorize every request.</summary>
public static class MobileShellAccessPolicy
{
    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/", "/dashboard", "/welcome", "/report", "/ellai/about", "/education", "/sessions", "/sessions/calendar",
        "/account", "/more", "/sync", "/about", "/privacy", "/terms", "/not-found", "/sign-in", "/forgot-password",
        "/reset-password", "/register", "/verify-email", "/welcome-complete"
    };

    public static bool IsPublicPath(string path) => PublicPaths.Contains(path);

    public static bool PublicEllaiOnly(bool authenticationResolved, bool signedIn) =>
        !authenticationResolved || !signedIn;
}
