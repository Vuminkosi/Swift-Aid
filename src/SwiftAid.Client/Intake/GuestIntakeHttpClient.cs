using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Client.Intake;

/// <summary>Calls only anonymous guest-intake endpoints; it never attaches a staff bearer token.</summary>
public sealed class GuestIntakeHttpClient : IGuestIntakeTransport
{
    private const string SessionPath = "public/v1/intake/sessions";
    private const string SubmissionPath = "public/v1/intake/reports";
    private const string ReceiptPath = "public/v1/intake/receipt";
    private const string ReceiptProofHeader = "X-SwiftAid-Receipt-Proof";
    private const string IntakeCapabilityHeader = "X-SwiftAid-Intake-Capability";
    private const string IntakeSessionHeader = "X-SwiftAid-Intake-Session";
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan CountryCatalogueRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public GuestIntakeHttpClient(HttpClient httpClient, bool allowInsecureTransport = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        var baseAddress = _httpClient.BaseAddress;
        if (baseAddress is null ||
            (!string.Equals(baseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
                !(allowInsecureTransport && string.Equals(baseAddress.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))) ||
            !string.IsNullOrEmpty(baseAddress.UserInfo) || !string.IsNullOrEmpty(baseAddress.Query) ||
            !string.IsNullOrEmpty(baseAddress.Fragment))
        {
            throw new ArgumentException("Guest intake requires a configured HTTPS service address.", nameof(httpClient));
        }
    }

    public async Task<IntakeSessionResponse> CreateSessionAsync(
        Guid sessionId,
        string receiptProof,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("An intake session is required.", nameof(sessionId));
        ValidateProof(receiptProof);
        using var response = await _httpClient.PostAsJsonAsync(
            SessionPath,
            new CreateIntakeSessionRequest(sessionId, receiptProof),
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var session = await ReadJsonAsync<IntakeSessionResponse>(response, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(session.Capability) || session.Capability.Length > 4096 ||
            session.Capability.Any(char.IsWhiteSpace) || session.Capability.Any(char.IsControl) ||
            session.ExpiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new GuestIntakeTransportException(isRetryable: false);
        }

        return session;
    }

    public async Task<IntakeReceipt> SubmitAsync(
        IntakeSubmission submission,
        Guid sessionId,
        string intakeCapability,
        string receiptProof,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (sessionId == Guid.Empty) throw new ArgumentException("An intake session is required.", nameof(sessionId));
        ValidateProof(receiptProof);
        ValidateHeaderValue(intakeCapability);

        using var request = new HttpRequestMessage(HttpMethod.Post, SubmissionPath)
        {
            Content = JsonContent.Create(submission, options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation(ReceiptProofHeader, receiptProof);
        request.Headers.TryAddWithoutValidation(IntakeCapabilityHeader, intakeCapability);
        request.Headers.TryAddWithoutValidation(IntakeSessionHeader, sessionId.ToString("D"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return await ReadJsonAsync<IntakeReceipt>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PublicIntakeMetadata> GetMetadataAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            "public/v1/intake/metadata",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var metadata = await ReadJsonAsync<PublicIntakeMetadata>(response, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(metadata.Version) ||
            string.IsNullOrWhiteSpace(metadata.CountryCatalogueVersion) ||
            !IsSafePublicReference(metadata.CountryCatalogueReference) ||
            string.IsNullOrWhiteSpace(metadata.PrivacyNoticeVersion) ||
            !IsSafePublicReference(metadata.PrivacyNoticeReference))
        {
            throw new GuestIntakeTransportException(isRetryable: true);
        }

        return metadata;
    }

    public async Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken cancellationToken)
    {
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestLifetime.CancelAfter(CountryCatalogueRequestTimeout);

        try
        {
            using var response = await _httpClient.GetAsync(
                "public/v1/intake/metadata/countries",
                HttpCompletionOption.ResponseHeadersRead,
                requestLifetime.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            var catalogue = await ReadJsonAsync<PublicIntakeCountryCatalogue>(response, requestLifetime.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(catalogue.Version) || !IsSafePublicReference(catalogue.Reference) ||
                catalogue.Countries is not { Count: > 0 } || catalogue.Countries.Count > 300 ||
                catalogue.Countries.Any(country => country is null || country.Code is not { Length: 2 } ||
                    country.Code.Any(character => character is < 'A' or > 'Z') ||
                    string.IsNullOrWhiteSpace(country.Label) || country.Label.Length > 200) ||
                catalogue.Countries.Select(country => country.Code).Distinct(StringComparer.Ordinal).Count() !=
                    catalogue.Countries.Count)
            {
                throw new GuestIntakeTransportException(isRetryable: true);
            }

            return catalogue;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GuestIntakeTransportException(isRetryable: true);
        }
    }

    private static bool IsSafePublicReference(string? reference) =>
        !string.IsNullOrWhiteSpace(reference) && reference.StartsWith("/", StringComparison.Ordinal) &&
        !reference.StartsWith("//", StringComparison.Ordinal) && !reference.Contains('\\') &&
        !reference.Contains("..", StringComparison.Ordinal) && !reference.Contains('?') &&
        !reference.Contains('#');

    public async Task<IntakeReceipt> GetReceiptAsync(
        Guid receiptId,
        string receiptProof,
        CancellationToken cancellationToken)
    {
        if (receiptId == Guid.Empty) throw new ArgumentException("A receipt identifier is required.", nameof(receiptId));
        ValidateProof(receiptProof);
        using var request = new HttpRequestMessage(HttpMethod.Post, ReceiptPath)
        {
            Content = JsonContent.Create(new IntakeReceiptQuery(receiptId), options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation(ReceiptProofHeader, receiptProof);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return await ReadJsonAsync<IntakeReceipt>(response, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateProof(string receiptProof)
    {
        if (string.IsNullOrWhiteSpace(receiptProof) || receiptProof.Length != 43 ||
            receiptProof.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("Receipt proof is invalid.", nameof(receiptProof));
        }
        byte[] proof;
        try
        {
            var normalized = receiptProof.Replace('-', '+').Replace('_', '/');
            normalized += (normalized.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw new FormatException()
            };
            proof = Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            throw new ArgumentException("Receipt proof is invalid.", nameof(receiptProof));
        }

        try
        {
            if (proof.Length != 32) throw new ArgumentException("Receipt proof is invalid.", nameof(receiptProof));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(proof);
        }
    }

    private static void ValidateHeaderValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
        {
            throw new ArgumentException("The intake session is invalid.", nameof(value));
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            throw new GuestIntakeTransportException(statusCode is 408 or 425 or 429 or >= 500);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes ||
            response.Content.Headers.ContentType?.MediaType is not "application/json")
        {
            throw new GuestIntakeTransportException(isRetryable: true);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes) throw new GuestIntakeTransportException(isRetryable: true);
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), JsonOptions)
                ?? throw new GuestIntakeTransportException(isRetryable: true);
        }
        catch (JsonException)
        {
            throw new GuestIntakeTransportException(isRetryable: true);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(chunk);
            if (buffer.TryGetBuffer(out var segment) && segment.Array is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(segment.Array.AsSpan());
            }
        }
    }
}

public sealed class GuestIntakeTransportException : Exception
{
    public GuestIntakeTransportException(bool isRetryable = true)
        : base("The report could not be sent or confirmed right now.") => IsRetryable = isRetryable;

    public bool IsRetryable { get; }
}
