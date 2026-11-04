using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Attachments;

namespace SwiftAid.Application.Attachments;

/// <summary>A single inclusive byte range within an attachment's original representation.</summary>
public readonly record struct AttachmentByteRange
{
    public AttachmentByteRange(long start, long endInclusive)
    {
        if (start < 0 || endInclusive < start)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "A single byte range must have a nonnegative start and an end at or after it.");
        }

        Start = start;
        EndInclusive = endInclusive;
    }

    public long Start { get; }

    public long EndInclusive { get; }

    public long Length => checked(EndInclusive - Start + 1);
}

/// <summary>Read stream for one authorized original-media range; caller must asynchronously dispose.</summary>
public sealed class AttachmentRangeContent : IAsyncDisposable
{
    public AttachmentRangeContent(Stream content, string contentType, AttachmentByteRange range, long totalLength)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The attachment range stream must be readable.", nameof(content));
        }

        if (string.IsNullOrWhiteSpace(contentType) || contentType.Any(char.IsControl))
        {
            throw new ArgumentException("A safe media content type is required.", nameof(contentType));
        }

        if (totalLength <= 0 || range.EndInclusive >= totalLength)
        {
            throw new ArgumentOutOfRangeException(nameof(totalLength), "The requested byte range must fit the authoritative representation length.");
        }

        Content = content;
        ContentType = contentType;
        Range = range;
        TotalLength = totalLength;
    }

    public Stream Content { get; }

    public string ContentType { get; }

    public AttachmentByteRange Range { get; }

    public long TotalLength { get; }

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// Resolves an attachment's immutable accepted server-side binding. Implementations must not infer
/// case scope from request data; null means no current accepted binding is available to this actor.
/// </summary>
public interface IAttachmentBindingResolver
{
    ValueTask<Guid?> ResolveAcceptedCaseIdAsync(
        AttachmentId attachmentId,
        ActorContext actor,
        CancellationToken cancellationToken);
}

/// <summary>
/// Application port for durable original-media transfer. Every method must resolve the current
/// accepted attachment binding and reauthorize <see cref="CaseOperation.ReadAttachment"/> using
/// the supplied server-created actor context. Status and offsets come from authoritative persisted
/// state. Chunk appends use compare-and-swap against <c>expectedOffset</c>; completion verifies the
/// full original hash before private promotion; range reads are authorized gateway-proxy streams.
/// This interface does not allow guest callers before the P2.4 upload-proof contract exists.
/// </summary>
public interface IAttachmentService
{
    ValueTask<AttachmentUploadStatus> ReserveAsync(
        ReserveAttachmentRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    ValueTask<AttachmentUploadStatus?> GetUploadStatusAsync(
        AttachmentId attachmentId,
        ActorContext actor,
        CancellationToken cancellationToken);

    ValueTask<AttachmentUploadStatus> AppendChunkAsync(
        AttachmentId attachmentId,
        long expectedOffset,
        Stream chunk,
        ActorContext actor,
        CancellationToken cancellationToken);

    ValueTask<AttachmentUploadStatus> CompleteAsync(
        CompleteAttachmentRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    ValueTask<AttachmentRangeContent?> OpenRangeAsync(
        AttachmentId attachmentId,
        AttachmentByteRange range,
        ActorContext actor,
        CancellationToken cancellationToken);
}
