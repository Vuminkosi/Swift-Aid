using Microsoft.Maui.Storage;

namespace SwiftAid.Mobile.Services.Welcome;

/// <summary>
/// Persists whether the first-run introduction carousel has been completed on
/// this device. Presentation progress only; never proof of identity,
/// membership, or authorization.
/// Constructed directly by onboarding pages: the shared composition root is
/// owned by another lane, and this store carries no dependencies.
/// </summary>
public interface IIntroCompletionStore
{
    bool IsCompleted();

    void MarkCompleted();
}

public sealed class IntroCompletionStore : IIntroCompletionStore
{
    private const string PreferenceKey = "swiftaid.mobile.intro-completed.v1";

    public bool IsCompleted()
    {
        try
        {
            return Preferences.Get(PreferenceKey, false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void MarkCompleted()
    {
        try
        {
            Preferences.Set(PreferenceKey, true);
        }
        catch (Exception)
        {
            // Device storage stays best-effort; callers already fail closed.
        }
    }
}
