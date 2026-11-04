using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SwiftAid.Client.Presentation;

public sealed class InvestigationHttpClient(HttpClient http) : IAiInvestigationClient
{
    public Task<UiCapabilityResult<Page<InvestigationWorkItem>>> ListAsync(PageRequest page, CancellationToken cancellationToken = default) => Send<Page<InvestigationWorkItem>>($"?page={page.PageNumber}&pageSize={page.PageSize}", null, cancellationToken);
    public Task<UiCapabilityResult<InvestigationSnapshot>> GetAsync(string investigationId, CancellationToken cancellationToken = default) => Send<InvestigationSnapshot>(Path(investigationId), null, cancellationToken);
    public Task<UiCapabilityResult<InvestigationSnapshot>> StartAsync(InvestigationRequest request, CancellationToken cancellationToken = default) => Send<InvestigationSnapshot>("", request, cancellationToken);
    public Task<UiCapabilityResult<InvestigationSnapshot>> ReplyAsync(string investigationId, long expectedVersion, string clarificationId, string factualAnswer, CancellationToken cancellationToken = default) => Send<InvestigationSnapshot>(Path(investigationId) + "/reply", new InvestigationReplyRequest(expectedVersion, clarificationId, factualAnswer), cancellationToken);
    public Task<UiCapabilityResult<InvestigationSnapshot>> PauseAsync(string investigationId, long expectedVersion, CancellationToken cancellationToken = default) => Send<InvestigationSnapshot>(Path(investigationId) + "/pause", new InvestigationVersionRequest(expectedVersion), cancellationToken);
    public Task<UiCapabilityResult<InvestigationSnapshot>> ResumeAsync(string investigationId, long expectedVersion, CancellationToken cancellationToken = default) => Send<InvestigationSnapshot>(Path(investigationId) + "/resume", new InvestigationVersionRequest(expectedVersion), cancellationToken);
    public Task<UiCapabilityResult<InvestigationSnapshot>> ProposeAdministrativeActionAsync(string investigationId, long expectedVersion, string summary, CancellationToken cancellationToken = default) => Send<InvestigationSnapshot>(Path(investigationId) + "/propose", new InvestigationProposalRequest(expectedVersion, summary), cancellationToken);
    public Task<UiCapabilityResult<InvestigationSnapshot>> ReviewProposalAsync(InvestigationProposalReview review, CancellationToken cancellationToken = default) => Send<InvestigationSnapshot>(Path(review.InvestigationId) + "/review", review, cancellationToken);
    public Task<UiCapabilityResult<InvestigationExecutionAndVerification>> GetExecutionAndVerificationAsync(string investigationId, CancellationToken cancellationToken = default) => Send<InvestigationExecutionAndVerification>(Path(investigationId) + "/execution", null, cancellationToken);
    private static string Path(string id) => "/" + Uri.EscapeDataString(id);
    private async Task<UiCapabilityResult<T>> Send<T>(string path, object? payload, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post, "api/v1/investigations" + path);
            if (payload is not null) request.Content = JsonContent.Create(payload, payload.GetType());
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return Failure<T>(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound => UiCapabilityResultCode.Denied,
                HttpStatusCode.Conflict => UiCapabilityResultCode.Conflict,
                HttpStatusCode.BadRequest => UiCapabilityResultCode.InvalidRequest,
                _ => UiCapabilityResultCode.Unavailable
            });
            return await response.Content.ReadFromJsonAsync<UiCapabilityResult<T>>(cancellationToken: ct) ?? Failure<T>(UiCapabilityResultCode.Unavailable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException) { return Failure<T>(UiCapabilityResultCode.Unavailable); }
    }
    private static UiCapabilityResult<T> Failure<T>(UiCapabilityResultCode code) => new(code, default,
        UiCapabilityResults.UnavailableMetadata("investigations", "The investigation request could not be confirmed. Refresh before retrying."));
}

public sealed record InvestigationVersionRequest(long ExpectedVersion);
public sealed record InvestigationReplyRequest(long ExpectedVersion, string ClarificationId, string FactualAnswer);
public sealed record InvestigationProposalRequest(long ExpectedVersion, string Summary);