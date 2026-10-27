using System.Text.Json.Serialization;

namespace SwiftAid.Contracts.Attachments;

/// <summary>
/// Opaque identifier for one attachment placeholder. It is not an authorization credential; every
/// server operation must resolve the accepted binding and authorize it independently.
/// </summary>
public readonly record struct AttachmentId
{
    [JsonConstructor]
    public AttachmentId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An attachment identifier must be non-empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

/// <summary>Closed set of original-media kinds supported by this transfer contract.</summary>
public enum AttachmentMediaKind
{
    Image,
    Audio,
    Video
}

/// <summary>Purpose is deliberately narrow; arbitrary documents are not accepted by this contract.</summary>
public enum AttachmentPurpose
{
    OriginalCaseComment
}

public enum AttachmentUploadState
{
    AwaitingUpload,
    Uploading,
    ServerVerified,
    Ready,
    Failed
}

/// <summary>An immutable SHA-256 value represented as lowercase hexadecimal.</summary>
public readonly record struct AttachmentSha256
{
    public const int ByteLength = 32;
    public const int HexLength = ByteLength * 2;

    [JsonConstructor]
    public AttachmentSha256(string hexValue)
    {
        ArgumentNullException.ThrowIfNull(hexValue);
        if (hexValue.Length != HexLength)
        {
            throw new ArgumentException("A SHA-256 value must contain exactly 64 hexadecimal characters.", nameof(hexValue));
        }

        try
        {
            _ = Convert.FromHexString(hexValue);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("A SHA-256 value must contain only hexadecimal characters.", nameof(hexValue), exception);
        }

        HexValue = hexValue.ToLowerInvariant();
    }

    public string HexValue { get; }

    public bool IsValid => HexValue is { Length: HexLength };

    public static AttachmentSha256 FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
        {
            throw new ArgumentException("A SHA-256 value must contain exactly 32 bytes.", nameof(bytes));
        }

        return new AttachmentSha256(Convert.ToHexString(bytes));
    }

    public static AttachmentSha256 Parse(string value)
    {
        return new AttachmentSha256(value);
    }

    public byte[] ToByteArray() => Convert.FromHexString(HexValue);

    public override string ToString() => HexValue;
}

/// <summary>
/// Reserves transfer for an attachment placeholder already bound to an accepted comment. The
/// request carries no case, comment, intake, object-key, bucket, or URL fields.
/// </summary>
public sealed record ReserveAttachmentRequest
{
    public ReserveAttachmentRequest(
        AttachmentId attachmentId,
        Guid requestId,
        long totalSizeBytes,
        AttachmentPurpose purpose,
        AttachmentMediaKind mediaKind,
        AttachmentSha256? expectedSha256 = null)
    {
        if (attachmentId.Value == Guid.Empty)
        {
            throw new ArgumentException("An attachment identifier is required.", nameof(attachmentId));
        }

        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty idempotency request identifier is required.", nameof(requestId));
        }

        if (totalSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSizeBytes), "The declared attachment size must be positive.");
        }

        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose));
        }

        if (!Enum.IsDefined(mediaKind))
        {
            throw new ArgumentOutOfRangeException(nameof(mediaKind));
        }

        if (expectedSha256 is { IsValid: false })
        {
            throw new ArgumentException("The optional digest must be a valid SHA-256 value.", nameof(expectedSha256));
        }

        AttachmentId = attachmentId;
        RequestId = requestId;
        TotalSizeBytes = totalSizeBytes;
        Purpose = purpose;
        MediaKind = mediaKind;
        ExpectedSha256 = expectedSha256;
    }

    public AttachmentId AttachmentId { get; }

    public Guid RequestId { get; }

    public long TotalSizeBytes { get; }

    public AttachmentPurpose Purpose { get; }

    public AttachmentMediaKind MediaKind { get; }

    /// <summary>May be absent at reservation and supplied at completion.</summary>
    public AttachmentSha256? ExpectedSha256 { get; }
}

/// <summary>Authoritative transfer state returned only after current case authorization.</summary>
public sealed record AttachmentUploadStatus
{
    public AttachmentUploadStatus(
        AttachmentId attachmentId,
        AttachmentUploadState state,
        long totalSizeBytes,
        long uploadOffset)
    {
        if (attachmentId.Value == Guid.Empty)
        {
            throw new ArgumentException("An attachment identifier is required.", nameof(attachmentId));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (totalSizeBytes <= 0 || uploadOffset < 0 || uploadOffset > totalSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(uploadOffset), "The persisted upload offset must be within the declared size.");
        }

        AttachmentId = attachmentId;
        State = state;
        TotalSizeBytes = totalSizeBytes;
        UploadOffset = uploadOffset;
    }

    public AttachmentId AttachmentId { get; }

    public AttachmentUploadState State { get; }

    public long TotalSizeBytes { get; }

    /// <summary>Authoritative persisted offset, suitable for resuming an interrupted transfer.</summary>
    public long UploadOffset { get; }
}

/// <summary>Completion supplies the expected digest when it was not known at reservation time.</summary>
public sealed record CompleteAttachmentRequest
{
    public CompleteAttachmentRequest(AttachmentId attachmentId, AttachmentSha256 sha256)
    {
        if (attachmentId.Value == Guid.Empty)
        {
            throw new ArgumentException("An attachment identifier is required.", nameof(attachmentId));
        }

        AttachmentId = attachmentId;
        if (!sha256.IsValid)
        {
            throw new ArgumentException("A valid SHA-256 value is required.", nameof(sha256));
        }

        Sha256 = sha256;
    }

    public AttachmentId AttachmentId { get; }

    public AttachmentSha256 Sha256 { get; }
}
