using System.Security.Cryptography;
using System.Text.Json;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Keeps the same identity for retries of an unchanged report in one circuit.</summary>
public sealed class WebReportSubmissionIdentity
{
    private byte[]? _lastDigest;
    private Guid _lastId;

    public IntakeSubmission Assign(IntakeSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.ClientSubmissionId != Guid.Empty)
            return submission;

        var payload = JsonSerializer.SerializeToUtf8Bytes(submission);
        byte[] digest;
        try
        {
            digest = SHA256.HashData(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }

        if (_lastDigest is null || !CryptographicOperations.FixedTimeEquals(_lastDigest, digest))
        {
            _lastDigest = digest;
            _lastId = Guid.NewGuid();
        }

        return submission with { ClientSubmissionId = _lastId };
    }
}
