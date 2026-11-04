using System.Net;
using System.Net.Http.Json;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Cases;

public sealed class CaseCorrelationHttpClient(HttpClient http) : ICaseCorrelationClient
{
    public async Task<CaseCorrelationPage?> GetPageAsync(Guid caseId, string? cursor, CancellationToken cancellationToken = default)
    {
        if (http.BaseAddress is null || caseId == Guid.Empty || cursor is { Length: > 60 }) return null;
        try
        {
            var suffix = cursor is null ? "" : "?cursor=" + Uri.EscapeDataString(cursor);
            using var response = await http.GetAsync($"api/v1/cases/{caseId:D}/correlation-candidates/page{suffix}", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return new(false, [], null);
            if (!response.IsSuccessStatusCode) return null;
            var page = await response.Content.ReadFromJsonAsync<CaseCorrelationPage>(cancellationToken).ConfigureAwait(false);
            if (page is null || page.Candidates is null || page.Candidates.Count > 20 || page.NextCursor is { Length: > 60 } ||
                page.Candidates.Any(item => item is null || item.CandidateId == Guid.Empty || item.RelatedCaseId == Guid.Empty || item.RelatedCaseId == caseId ||
                    item.RecordedAtUtc == default || item.Reasons is null || item.Reasons.Count > 5 || item.Decision is { } d && !Enum.IsDefined(d) ||
                    item.Reasons.Any(reason => reason is not ("SameSourceIdentifier" or "SameSector" or "SharedRegion" or "ReportsCloseInTime" or "SemanticCandidate")))) return null;
            return page;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException or NotSupportedException) { return null; }
    }
    public async Task<OrganizationDecisionResult> DecideAsync(Guid caseId, Guid candidateId, OrganizationDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (http.BaseAddress is null || caseId == Guid.Empty || candidateId == Guid.Empty || request is null || request.CommandId == Guid.Empty)
            return new(OrganizationDecisionOutcome.Unavailable);
        try
        {
            using var response = await http.PostAsJsonAsync($"api/v1/cases/{caseId:D}/correlation-candidates/{candidateId:D}/decision", request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return new(OrganizationDecisionOutcome.NotFoundOrNotAuthorized);
            if (response.StatusCode == HttpStatusCode.Conflict) return new(OrganizationDecisionOutcome.Conflict);
            if (!response.IsSuccessStatusCode) return new(OrganizationDecisionOutcome.Unavailable);
            var result = await response.Content.ReadFromJsonAsync<OrganizationDecisionResult>(cancellationToken).ConfigureAwait(false);
            return result is not null && Enum.IsDefined(result.Status) ? result : new(OrganizationDecisionOutcome.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException or NotSupportedException) { return new(OrganizationDecisionOutcome.Unavailable); }
    }
    public async Task<CaseCorrelationReadResult?> GetAsync(Guid caseId, CancellationToken cancellationToken = default)
    {
        if (http.BaseAddress is null || caseId == Guid.Empty) return null;
        try
        {
            using var response = await http.GetAsync($"api/v1/cases/{caseId:D}/correlation-candidates", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return new(false, []);
            if (!response.IsSuccessStatusCode) return null;
            var candidates = await response.Content.ReadFromJsonAsync<CaseCorrelationView[]>(cancellationToken).ConfigureAwait(false);
            if (candidates is null || candidates.Length > 100 || candidates.Any(item => item is null || item.CandidateId == Guid.Empty ||
                item.RelatedCaseId == Guid.Empty || item.RelatedCaseId == caseId || item.RecordedAtUtc == default || item.Reasons is null ||
                item.Reasons.Count > 5 || item.Decision is { } decision && !Enum.IsDefined(decision) ||
                item.Reasons.Any(reason => reason is not ("SameSourceIdentifier" or "SameSector" or "SharedRegion" or "ReportsCloseInTime" or "SemanticCandidate")))) return null;
            return new(true, candidates);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException or NotSupportedException) { return null; }
    }
}
