namespace SwiftAid.Application.Abstractions;

/// <summary>
/// Opaque identifier for a privately stored object. It deliberately contains no bucket, path,
/// organization, or public URL; authorization and ownership are resolved separately.
/// </summary>
public sealed record ObjectReference
{
    public ObjectReference(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A non-empty object identifier is required.", nameof(id));
        }

        Id = id;
    }

    public Guid Id { get; }
}
