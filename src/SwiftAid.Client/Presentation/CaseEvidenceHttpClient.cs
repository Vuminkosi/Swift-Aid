using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace SwiftAid.Client.Presentation;

/// <summary>Shared native/browser transport; authentication is supplied by the host's HTTP handler.</summary>
public sealed class CaseEvidenceHttpClient(HttpClient http) : ICaseEvidenceClient, ICaseEvidenceUploadClient,
    ICaseEvidenceContentClient, ICaseObservationClient
{
    public Task<UiCapabilityResult<CaseEvidenceSnapshot>> GetAsync(Guid caseId, CaseEvidenceQuery query, CancellationToken cancellationToken = default) =>
        SendAsync<CaseEvidenceSnapshot>(new(HttpMethod.Get, $"api/v1/cases/{caseId:D}/evidence?page={query.Page.PageNumber}&pageSize={query.Page.PageSize}"), cancellationToken);

    public Task<UiCapabilityResult<CaseObservationSnapshot>> GetAsync(Guid caseId, PageRequest page, CancellationToken cancellationToken = default) =>
        SendAsync<CaseObservationSnapshot>(new(HttpMethod.Get, $"api/v1/cases/{caseId:D}/observations?page={page.PageNumber}&pageSize={page.PageSize}"), cancellationToken);

    public Task<UiCapabilityResult<CaseObservationWriteResult>> RecordAsync(CaseObservationCommand command, CancellationToken cancellationToken = default) =>
        SendAsync<CaseObservationWriteResult>(new(HttpMethod.Post, $"api/v1/cases/{command.CaseId:D}/observations")
        { Content = JsonContent.Create(command) }, cancellationToken);

    public Task<UiCapabilityResult<CaseEvidenceDetail>> GetDetailAsync(Guid caseId, string evidenceKey, CancellationToken cancellationToken = default) =>
        SendAsync<CaseEvidenceDetail>(new(HttpMethod.Get, $"api/v1/cases/{caseId:D}/evidence/{Uri.EscapeDataString(evidenceKey)}"), cancellationToken);

    public async Task<UiCapabilityResult<CaseEvidenceUploadResult>> UploadAsync(CaseEvidenceUploadRequest request, Stream content,
        CancellationToken cancellationToken = default)
    {
        if (!request.IsValid) return Failure<CaseEvidenceUploadResult>(UiCapabilityResultCode.InvalidRequest);
        try
        {
            var bytes = await ReadExactBoundedAsync(content, request.DeclaredContentLength, cancellationToken);
            using var message = new HttpRequestMessage(HttpMethod.Post,
                $"api/v1/cases/{request.CaseId:D}/evidence/{request.SubmissionId:D}?expectedVersion={request.ExpectedVersion}&fileName={Uri.EscapeDataString(request.GetCanonicalFileName())}");
            message.Content = new ByteArrayContent(bytes);
            message.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(request.DeclaredContentType);
            return await SendAsync<CaseEvidenceUploadResult>(message, cancellationToken);
        }
        catch (InvalidDataException) { return Failure<CaseEvidenceUploadResult>(UiCapabilityResultCode.InvalidRequest); }
    }

    public async Task<UiCapabilityResult<CaseEvidenceContentMetadata>> DownloadToAsync(Guid caseId, string evidenceKey,
        Stream destination, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync($"api/v1/cases/{caseId:D}/evidence/{Uri.EscapeDataString(evidenceKey)}/content",
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return Failure<CaseEvidenceContentMetadata>(Status(response));
            if (!response.Headers.TryGetValues("X-SwiftAid-Evidence", out var values)) return Failure<CaseEvidenceContentMetadata>();
            var header = values.Single();
            if (header.Length > 4096) return Failure<CaseEvidenceContentMetadata>();
            var metadata = JsonSerializer.Deserialize<CaseEvidenceContentMetadata>(Convert.FromBase64String(header));
            if (metadata is not { IsValid: true } || metadata.CaseId != caseId || metadata.EvidenceKey != evidenceKey)
                return Failure<CaseEvidenceContentMetadata>();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = await ReadExactBoundedAsync(stream, metadata.ByteLength, cancellationToken);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), metadata.Sha256, StringComparison.OrdinalIgnoreCase))
                return Failure<CaseEvidenceContentMetadata>();
            await destination.WriteAsync(bytes, cancellationToken);
            return new(UiCapabilityResultCode.Success, metadata, Metadata(true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or FormatException or InvalidOperationException)
        { return Failure<CaseEvidenceContentMetadata>(); }
    }

    public async Task<UiCapabilityResult<CaseEvidenceDownload>> DownloadAsync(Guid caseId, string evidenceKey, CancellationToken cancellationToken = default)
    {
        await using var stream = new MemoryStream();
        var result = await DownloadToAsync(caseId, evidenceKey, stream, cancellationToken);
        if (!result.Succeeded || result.Value is not { } metadata) return Failure<CaseEvidenceDownload>(result.Code);
        if (metadata.ByteLength > CaseEvidenceDownload.MaximumContentBytes || metadata.ContentType != "text/plain; charset=utf-8")
            return Failure<CaseEvidenceDownload>(UiCapabilityResultCode.InvalidRequest);
        return new(UiCapabilityResultCode.Success, new(caseId, metadata.AggregateVersion, evidenceKey,
            metadata.FileName, metadata.ContentType, metadata.ByteLength, metadata.Sha256, stream.ToArray()), Metadata(true));
    }

    private async Task<UiCapabilityResult<T>> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            try
            {
                using var response = await http.SendAsync(request, ct);
                var result = await response.Content.ReadFromJsonAsync<UiCapabilityResult<T>>(cancellationToken: ct);
                return result ?? Failure<T>(Status(response));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException)
            { return Failure<T>(); }
        }
    }

    private static async Task<byte[]> ReadExactBoundedAsync(Stream stream, long length, CancellationToken ct)
    {
        if (length is <= 0 or > CaseEvidenceUploadRequest.MaximumContentBytes) throw new InvalidDataException("Invalid file size.");
        var bytes = new byte[checked((int)length)];
        try { await stream.ReadExactlyAsync(bytes, ct); }
        catch (EndOfStreamException error) { throw new InvalidDataException("Incomplete file.", error); }
        if (await stream.ReadAsync(new byte[1], ct) != 0) throw new InvalidDataException("Unexpected file length.");
        return bytes;
    }

    private static UiCapabilityResultCode Status(HttpResponseMessage response) => response.StatusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound => UiCapabilityResultCode.Denied,
        System.Net.HttpStatusCode.Conflict => UiCapabilityResultCode.Conflict,
        System.Net.HttpStatusCode.BadRequest => UiCapabilityResultCode.InvalidRequest,
        _ => UiCapabilityResultCode.Unavailable
    };
    private static UiCapabilityMetadata Metadata(bool available) => new("case-evidence", UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, available);
    private static UiCapabilityResult<T> Failure<T>(UiCapabilityResultCode code = UiCapabilityResultCode.Unavailable) => new(code, default, Metadata(false));
}
