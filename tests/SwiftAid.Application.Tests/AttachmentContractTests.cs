using System.Reflection;
using System.Text.Json;
using SwiftAid.Application.Attachments;
using SwiftAid.Contracts.Attachments;

namespace SwiftAid.Application.Tests;

public sealed class AttachmentContractTests
{
    private static readonly AttachmentId AttachmentId = new(Guid.Parse("aa629761-762b-4674-a4ad-94a78396f11b"));
    private static readonly Guid RequestId = Guid.Parse("16e741f5-f4cf-4770-acd4-374ee0ba8cc3");
    private static readonly AttachmentSha256 Hash = AttachmentSha256.FromBytes(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

    [Fact]
    public void Attachment_id_requires_a_nonempty_guid_and_is_not_a_credential()
    {
        Assert.Throws<ArgumentException>(() => new AttachmentId(Guid.Empty));
        Assert.Equal(Guid.Parse("aa629761-762b-4674-a4ad-94a78396f11b"), AttachmentId.Value);
    }

    [Theory]
    [InlineData(AttachmentMediaKind.Image)]
    [InlineData(AttachmentMediaKind.Audio)]
    [InlineData(AttachmentMediaKind.Video)]
    public void Reservation_accepts_only_purpose_scoped_supported_media_and_optional_sha(AttachmentMediaKind kind)
    {
        var request = new ReserveAttachmentRequest(
            AttachmentId,
            RequestId,
            totalSizeBytes: 1024,
            AttachmentPurpose.OriginalCaseComment,
            kind);

        Assert.Equal(AttachmentId, request.AttachmentId);
        Assert.Equal(RequestId, request.RequestId);
        Assert.Null(request.ExpectedSha256);
        Assert.Equal(kind, request.MediaKind);
    }

    [Fact]
    public void Reservation_rejects_empty_ids_nonpositive_size_and_unknown_media_values()
    {
        Assert.Throws<ArgumentException>(() => new ReserveAttachmentRequest(
            AttachmentId,
            Guid.Empty,
            1024,
            AttachmentPurpose.OriginalCaseComment,
            AttachmentMediaKind.Audio));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReserveAttachmentRequest(
            AttachmentId,
            RequestId,
            0,
            AttachmentPurpose.OriginalCaseComment,
            AttachmentMediaKind.Audio));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReserveAttachmentRequest(
            AttachmentId,
            RequestId,
            1024,
            AttachmentPurpose.OriginalCaseComment,
            (AttachmentMediaKind)int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReserveAttachmentRequest(
            AttachmentId,
            RequestId,
            1024,
            (AttachmentPurpose)int.MaxValue,
            AttachmentMediaKind.Audio));
    }

    [Fact]
    public void Reservation_request_contains_no_case_comment_or_guest_intake_identifiers()
    {
        var propertyNames = typeof(ReserveAttachmentRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name => name.Contains("Case", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Comment", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Intake", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("ObjectKey", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Url", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sha256_value_is_exactly_32_bytes_and_copied_on_input_and_output()
    {
        var source = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var hash = AttachmentSha256.FromBytes(source);
        source[0] = 255;
        var bytes = hash.ToByteArray();
        bytes[1] = 255;

        Assert.Equal("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", hash.HexValue);
        Assert.Equal((byte)0, hash.ToByteArray()[0]);
        Assert.Equal((byte)1, hash.ToByteArray()[1]);
        Assert.Throws<ArgumentException>(() => AttachmentSha256.FromBytes(new byte[31]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Sha256_parser_rejects_invalid_text(string value)
    {
        Assert.Throws<ArgumentException>(() => AttachmentSha256.Parse(value));
    }

    [Fact]
    public void Sha256_parser_normalizes_hex_case()
    {
        var parsed = AttachmentSha256.Parse("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

        Assert.Equal(Hash, parsed);
        Assert.Equal("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", parsed.HexValue);
    }

    [Fact]
    public void Shared_transfer_dto_roundtrips_through_system_text_json()
    {
        var request = new ReserveAttachmentRequest(
            AttachmentId,
            RequestId,
            totalSizeBytes: 1024,
            AttachmentPurpose.OriginalCaseComment,
            AttachmentMediaKind.Audio,
            Hash);

        var json = JsonSerializer.Serialize(request);
        var restored = JsonSerializer.Deserialize<ReserveAttachmentRequest>(json);

        Assert.NotNull(restored);
        Assert.Equal(request.AttachmentId, restored.AttachmentId);
        Assert.Equal(request.RequestId, restored.RequestId);
        Assert.Equal(request.ExpectedSha256, restored.ExpectedSha256);
    }

    [Fact]
    public void Persisted_offset_must_stay_within_declared_total()
    {
        var status = new AttachmentUploadStatus(AttachmentId, AttachmentUploadState.Uploading, 100, 40);

        Assert.Equal(40, status.UploadOffset);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AttachmentUploadStatus(
            AttachmentId,
            AttachmentUploadState.Uploading,
            100,
            101));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 19)]
    [InlineData(4, 7)]
    public void Single_range_accepts_valid_inclusive_bounds(long start, long endInclusive)
    {
        var range = new AttachmentByteRange(start, endInclusive);

        Assert.Equal(endInclusive - start + 1, range.Length);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(4, 3)]
    public void Single_range_rejects_invalid_bounds(long start, long endInclusive)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AttachmentByteRange(start, endInclusive));
    }

    [Fact]
    public async Task Range_content_requires_readable_stream_and_disposes_it()
    {
        var stream = new MemoryStream([1, 2, 3]);
        var content = new AttachmentRangeContent(stream, "audio/ogg", new AttachmentByteRange(0, 2), totalLength: 3);

        Assert.Equal("audio/ogg", content.ContentType);
        Assert.Equal(3, content.Range.Length);
        Assert.Equal(3, content.TotalLength);
        await content.DisposeAsync();
        Assert.False(stream.CanRead);
    }

    [Fact]
    public void Range_content_rejects_end_outside_authoritative_total()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AttachmentRangeContent(
            new MemoryStream([1, 2, 3]),
            "audio/ogg",
            new AttachmentByteRange(0, 3),
            totalLength: 3));
    }
}
