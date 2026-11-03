namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>Local, value-free validation guidance for the mounted form and its Ellai host.</summary>
public sealed class EllaiValidationGuidance
{
    public event Action<string?>? Changed;

    public void Publish(string? message) => Changed?.Invoke(message);
}
