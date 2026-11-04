using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai;

/// <summary>Circuit-local Ellai launcher. Menus publish the chosen mode;
/// the guidance host subscribes and opens the composer. No subscribers
/// means no host on this circuit, and the menu keeps its local fallback.</summary>
public sealed class EllaiLauncher
{
    public event Action<EllaiMode>? Requested;

    public bool HasSubscribers => Requested is not null;

    public void Request(EllaiMode mode) => Requested?.Invoke(mode);
}
