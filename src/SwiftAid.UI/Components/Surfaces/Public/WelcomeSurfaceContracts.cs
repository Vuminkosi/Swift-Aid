namespace SwiftAid.UI.Components.Surfaces.Public;

public enum WelcomeNavigationIntent
{
    CommunityHealthWorker,
    HealthOfficer,
    PublicUser
}

public sealed record WelcomeContinueRequest(
    WelcomeNavigationIntent Intent,
    string? CountryCode);
