using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SwiftAid.Contracts.Intake.Imports;

namespace SwiftAid.Client.Intake.Imports;

/// <summary>Authenticated, bounded transport for resumable workspace imports.</summary>
public sealed class WorkspaceImportHttpClient(HttpClient httpClient) : IWorkspaceImportClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<WorkspaceImportScope>> GetWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync<WorkspaceImportScope[]>("api/v1/case-imports/workspaces", cancellationToken).ConfigureAwait(false);
        if (result.Length > 100 || result.Any(item => item is null || string.IsNullOrWhiteSpace(item.WorkspaceId) ||
            string.IsNullOrWhiteSpace(item.OrganizationId) || item.CountryCode is null || item.CountryCode.Length != 2))
            throw new WorkspaceImportException("invalid-response");
        return result;
    }

    public async Task<IReadOnlyList<WorkspaceImportHistoryItem>> GetRecentAsync(WorkspaceImportScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var path = $"api/v1/case-imports/recent?workspaceId={Uri.EscapeDataString(scope.WorkspaceId)}" +
            $"&organizationId={Uri.EscapeDataString(scope.OrganizationId)}&countryCode={Uri.EscapeDataString(scope.CountryCode)}";
        var result = await ReadAsync<WorkspaceImportHistoryItem[]>(path, cancellationToken).ConfigureAwait(false);
        if (result.Length > 20 || result.Any(item => item is null || item.ImportId == Guid.Empty ||
            item.WorkspaceId != scope.WorkspaceId || item.CountryCode != scope.CountryCode || string.IsNullOrWhiteSpace(item.State)))
            throw new WorkspaceImportException("invalid-response");
        return result;
    }

    public async Task<WorkspaceImportSummary?> GetAsync(Guid importId, CancellationToken cancellationToken = default)
    {
        if (importId == Guid.Empty) throw new WorkspaceImportException("invalid-request");
        using var response = await httpClient.GetAsync($"api/v1/case-imports/{importId:D}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<WorkspaceImportSummary>(Json, cancellationToken).ConfigureAwait(false);
        if (result is null || result.ImportId != importId || result.NextRow < 1 || string.IsNullOrWhiteSpace(result.State))
            throw new WorkspaceImportException("invalid-response");
        return result;
    }

    public async Task<IReadOnlyList<WorkspaceImportRow>> GetIssuesAsync(Guid importId, long afterRow, CancellationToken cancellationToken = default)
    {
        if (importId == Guid.Empty || afterRow < 0) throw new WorkspaceImportException("invalid-request");
        var result = await ReadAsync<WorkspaceImportRow[]>($"api/v1/case-imports/{importId:D}/issues?afterRow={afterRow}", cancellationToken).ConfigureAwait(false);
        if (result.Length > 20 || result.Any(item => item is null || item.RowNumber <= afterRow || string.IsNullOrWhiteSpace(item.Status)) ||
            result.Select(item => item.RowNumber).Distinct().Count() != result.Length ||
            !result.Select(item => item.RowNumber).SequenceEqual(result.Select(item => item.RowNumber).Order()))
            throw new WorkspaceImportException("invalid-response");
        return result;
    }

    public async Task<bool> MatchesContextAsync(Guid importId, WorkspaceImportContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (importId == Guid.Empty || !HasValidFingerprint(context.FileSha256)) throw new WorkspaceImportException("invalid-request");
        using var response = await httpClient.PostAsJsonAsync($"api/v1/case-imports/{importId:D}/context-check", context, Json, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<ContextCheckResponse>(Json, cancellationToken).ConfigureAwait(false);
        return result?.Matches ?? throw new WorkspaceImportException("invalid-response");
    }

    public async Task<WorkspaceImportBatchResult> ImportAsync(Guid importId, WorkspaceImportContext context, CaseCsvBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(batch);
        if (importId == Guid.Empty || !HasValidFingerprint(context.FileSha256) || batch.StartRow < 1 || batch.RowCount is < 0 or > CaseCsvBatchReader.MaximumBatchRows ||
            Encoding.UTF8.GetByteCount(batch.Csv) > CaseCsvBatchReader.MaximumBatchBytes)
            throw new WorkspaceImportException("invalid-request");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"api/v1/case-imports/{importId:D}/rows?startRow={batch.StartRow}&finalBatch={batch.IsFinal.ToString().ToLowerInvariant()}");
        request.Headers.TryAddWithoutValidation("X-SwiftAid-Import", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(context, Json)));
        request.Content = new StringContent(batch.Csv, Encoding.UTF8, "text/csv");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var result = await response.Content.ReadFromJsonAsync<WorkspaceImportBatchResult>(Json, cancellationToken).ConfigureAwait(false);
            if (result is null || result.ImportId != importId || result.Rows is null || result.Rows.Count > batch.RowCount ||
                result.Rows.Where((row, index) => row is null || row.RowNumber != batch.StartRow + index || string.IsNullOrWhiteSpace(row.Status)).Any() ||
                result.Summary is { } summary && (summary.ImportId != importId || summary.NextRow < 1) ||
                result.ErrorCode is null && (result.Summary is null || result.Rows.Count != batch.RowCount ||
                    result.Summary.NextRow != batch.StartRow + batch.RowCount || batch.IsFinal && result.Summary.State != "Completed"))
                throw new WorkspaceImportException("invalid-response");
            return result;
        }
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        throw new WorkspaceImportException("invalid-response");
    }

    private async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkspaceImportException("invalid-response");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String &&
                code.GetString() is { Length: > 0 and <= 80 } value)
                throw new WorkspaceImportException(value);
        }
        catch (JsonException) { }
        throw new WorkspaceImportException(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
            ? "not-authorized" : "unavailable");
    }

    private static bool HasValidFingerprint(string? value) => value is null || value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record ContextCheckResponse(bool? Matches);
}
