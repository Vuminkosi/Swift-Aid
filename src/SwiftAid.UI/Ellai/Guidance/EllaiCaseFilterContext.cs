using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>Connects one mounted case list to its existing query callback; contains no record data.</summary>
public sealed class EllaiCaseFilterContext
{
    private readonly Dictionary<object, Func<UiIntentProposal, CancellationToken, Task<bool>>> _mounted = [];
    private readonly object _gate = new();
    public void Register(object owner, Func<UiIntentProposal, CancellationToken, Task<bool>> apply)
    {
        lock (_gate) _mounted[owner] = apply;
    }
    public void Remove(object owner) { lock (_gate) _mounted.Remove(owner); }
    public Task<bool> ApplyAsync(UiIntentProposal proposal, CancellationToken cancellationToken)
    {
        Func<UiIntentProposal, CancellationToken, Task<bool>>? apply;
        lock (_gate) apply = _mounted.Count == 1 ? _mounted.Values.Single() : null;
        return apply is null || !EllaiCaseFilterProposal.IsValid(proposal) ? Task.FromResult(false) : apply(proposal, cancellationToken);
    }
}
