using System.Linq.Expressions;
using CommunityToolkit.Datasync.Server;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace SwiftAid.Gateway.Controllers;

/// <summary>
/// Temporary authenticated, read-only Datasync spike. The fixed rows are synthetic and must never
/// be used as a production projection.
/// </summary>
[ApiController]
[Authorize]
[Route("mobile/api/tables/spike")]
public sealed class SpikeTableController
    : TableController<OfflineSpikeTableRow>
{
    public SpikeTableController()
        : base(new SyntheticSpikeRepository(), new ReadOnlySpikeAccessControlProvider())
    {
    }
}

/// <summary>Server representation whose metadata matches Datasync 10.1.2 ITableData.</summary>
public sealed class OfflineSpikeTableRow : ITableData, IEquatable<ITableData>
{
    public string Id { get; set; } = string.Empty;

    public bool Deleted { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public byte[] Version { get; set; } = [];

    public string Value { get; set; } = string.Empty;

    public bool Equals(ITableData? other) =>
        other is OfflineSpikeTableRow row &&
        string.Equals(Id, row.Id, StringComparison.Ordinal) &&
        Deleted == row.Deleted &&
        UpdatedAt == row.UpdatedAt &&
        Version.AsSpan().SequenceEqual(row.Version) &&
        string.Equals(Value, row.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ITableData other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id, StringComparer.Ordinal);
        hash.Add(Deleted);
        hash.Add(UpdatedAt);
        hash.Add(Value, StringComparer.Ordinal);
        foreach (var value in Version)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}

internal sealed class ReadOnlySpikeAccessControlProvider : AccessControlProvider<OfflineSpikeTableRow>
{
    public override Expression<Func<OfflineSpikeTableRow, bool>>? GetDataView() => row => !row.Deleted;

    public override ValueTask<bool> IsAuthorizedAsync(
        TableOperation operation,
        OfflineSpikeTableRow? entity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(operation == TableOperation.Query);
    }
}

internal sealed class SyntheticSpikeRepository : IRepository<OfflineSpikeTableRow>
{
    private static readonly OfflineSpikeTableRow[] Rows =
    [
        new()
        {
            Id = "offline-spike-1",
            Value = "Synthetic Datasync row",
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Version = [1],
            Deleted = false
        }
    ];

    public ValueTask<IQueryable<OfflineSpikeTableRow>> AsQueryableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Rows.AsQueryable());
    }

    public ValueTask CreateAsync(OfflineSpikeTableRow entity, CancellationToken cancellationToken) =>
        ReadOnly(cancellationToken);

    public ValueTask DeleteAsync(string id, byte[]? version, CancellationToken cancellationToken) =>
        ReadOnly(cancellationToken);

    public ValueTask<OfflineSpikeTableRow> ReadAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var row = Rows.SingleOrDefault(value => string.Equals(value.Id, id, StringComparison.Ordinal));
        return row is null
            ? ValueTask.FromException<OfflineSpikeTableRow>(new HttpException(StatusCodes.Status404NotFound))
            : ValueTask.FromResult(row);
    }

    public ValueTask ReplaceAsync(OfflineSpikeTableRow? entity, byte[]? version, CancellationToken cancellationToken) =>
        ReadOnly(cancellationToken);

    public ValueTask<int> CountAsync(IQueryable<OfflineSpikeTableRow> query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(query.Count());
    }

    public ValueTask<IList<OfflineSpikeTableRow>> ToListAsync(
        IQueryable<OfflineSpikeTableRow> query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IList<OfflineSpikeTableRow>>(query.ToList());
    }

    private static ValueTask ReadOnly(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException(new HttpException(StatusCodes.Status405MethodNotAllowed, "The Datasync spike is read-only."));
    }
}
