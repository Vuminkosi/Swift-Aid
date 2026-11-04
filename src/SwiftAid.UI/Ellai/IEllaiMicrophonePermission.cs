namespace SwiftAid.UI.Ellai;

/// <summary>Optional native permission bridge for Blazor Hybrid hosts. Browser
/// hosts rely on the explicit getUserMedia permission prompt.</summary>
public interface IEllaiMicrophonePermission
{
    Task<bool> RequestAsync(CancellationToken cancellationToken);
}
