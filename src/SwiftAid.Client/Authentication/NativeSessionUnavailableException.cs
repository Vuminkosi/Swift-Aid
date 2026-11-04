namespace SwiftAid.Client.Authentication;

/// <summary>Indicates that secure session material could not be read safely.</summary>
public sealed class NativeSessionUnavailableException : Exception
{
    public NativeSessionUnavailableException()
        : base("The saved sign-in session is unavailable.")
    {
    }
}
