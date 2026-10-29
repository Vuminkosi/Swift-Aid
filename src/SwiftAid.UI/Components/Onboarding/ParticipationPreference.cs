namespace SwiftAid.UI.Components.Onboarding;

/// <summary>
/// A user's stated participation preference captured during onboarding.
/// A preference personalizes explanatory content or starts a membership request.
/// It never creates officer, clinical, administrative, or case-access grants;
/// actual permissions come only from verified organizational membership.
/// </summary>
public enum ParticipationPreference
{
    CommunityHealthWorker = 0,
    NurseClinician = 1,
    CommunityLeader = 2,
    ProgramCoordinator = 3,
    CommunityMember = 4
}

public sealed record ParticipationPreferenceOption(
    ParticipationPreference Preference,
    string Title,
    string Description);

public static class ParticipationPreferences
{
    /// <summary>
    /// Selecting a preference grants no permissions. This constant exists so
    /// presenters and tests can assert the invariant directly.
    /// </summary>
    public const bool SelectionGrantsPermissions = false;

    public static IReadOnlyList<ParticipationPreferenceOption> All { get; } =
    [
        new(ParticipationPreference.CommunityHealthWorker, "Community Health Worker",
            "Fieldwork, case coordination, community education"),
        new(ParticipationPreference.NurseClinician, "Nurse / Clinician",
            "Clinical or health services"),
        new(ParticipationPreference.CommunityLeader, "Community Leader",
            "Outreach and engagement"),
        new(ParticipationPreference.ProgramCoordinator, "Program Coordinator",
            "Planning and reporting"),
        new(ParticipationPreference.CommunityMember, "Community Member",
            "Public information and events")
    ];
}
