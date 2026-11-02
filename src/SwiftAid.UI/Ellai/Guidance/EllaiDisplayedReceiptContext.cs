using SwiftAid.Contracts.Intake;

namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>Current rendered receipt references only; never contains ownership proofs and
/// never grants completion authority. Ambiguous simultaneous receipts fail closed.</summary>
public sealed class EllaiDisplayedReceiptContext
{
    private readonly object _gate = new();
    private readonly Dictionary<object, IntakeReceipt> _mounted = [];
    public IntakeReceipt? Current
    {
        get
        {
            lock (_gate) return _mounted.Values.Distinct().Take(2).ToArray() is { Length: 1 } receipts ? receipts[0] : null;
        }
    }
    public void Update(object owner, IntakeReceipt? receipt)
    {
        lock (_gate)
        {
            if (receipt is null) _mounted.Remove(owner);
            else _mounted[owner] = receipt;
        }
    }
    public void Remove(object owner) { lock (_gate) _mounted.Remove(owner); }
}
