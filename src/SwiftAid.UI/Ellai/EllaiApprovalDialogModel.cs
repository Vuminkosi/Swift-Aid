namespace SwiftAid.UI.Ellai;

/// <summary>One selectable action row in the approval dialog.</summary>
public sealed record EllaiApprovalActionRow(
    string ActionId,
    string Label,
    string Detail,
    bool Selected);

/// <summary>Selection state for the approval dialog. Approve is enabled only
/// after explicit selection; nothing is preselected. A revised plan version
/// resets to a fresh unselected set: prior selection never carries over.</summary>
public sealed class EllaiApprovalDialogModel
{
    private readonly Dictionary<string, EllaiApprovalActionRow> _rows = new(StringComparer.Ordinal);

    public EllaiApprovalDialogModel(IEnumerable<EllaiApprovalActionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            _rows[row.ActionId] = row with { Selected = false };
        }
    }

    public IReadOnlyList<EllaiApprovalActionRow> Rows => _rows.Values.ToList();

    public bool CanApprove => _rows.Values.Any(row => row.Selected);

    public IReadOnlyList<string> SelectedIds => _rows.Values
        .Where(row => row.Selected)
        .Select(row => row.ActionId)
        .ToList();

    public void Toggle(string actionId)
    {
        if (_rows.TryGetValue(actionId, out var row))
        {
            _rows[actionId] = row with { Selected = !row.Selected };
        }
    }
}
