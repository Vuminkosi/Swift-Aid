using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SwiftAid.Client.Presentation;

public sealed class CaseDiscussionHttpClient(HttpClient http) : ICaseDiscussionClient, ICaseDiscussionCommandClient
{
    public Task<UiCapabilityResult<CaseDiscussionSnapshot>> GetAsync(Guid caseId, CaseDiscussionQuery query,
        CancellationToken cancellationToken = default) => !query.IsValid || caseId == Guid.Empty
        ? Task.FromResult(Failure<CaseDiscussionSnapshot>(UiCapabilityResultCode.InvalidRequest))
        : SendAsync<CaseDiscussionSnapshot>(new(HttpMethod.Get,
            $"api/v1/cases/{caseId:D}/discussion?page={query.Page.PageNumber}&pageSize={query.Page.PageSize}"), cancellationToken);

    public Task<UiCapabilityResult<IReadOnlyList<CaseDiscussionMentionTarget>>> GetMentionTargetsAsync(Guid caseId,
        CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<CaseDiscussionMentionTarget>>(
            new(HttpMethod.Get, $"api/v1/cases/{caseId:D}/discussion/mentions"), cancellationToken);

    public Task<UiCapabilityResult<CaseDiscussionPostResult>> AppendAsync(CaseDiscussionPostCommand command,
        CancellationToken cancellationToken = default) => !command.IsValid
        ? Task.FromResult(Failure<CaseDiscussionPostResult>(UiCapabilityResultCode.InvalidRequest))
        : SendAsync<CaseDiscussionPostResult>(new(HttpMethod.Post, $"api/v1/cases/{command.CaseId:D}/discussion")
            { Content = JsonContent.Create(command) }, cancellationToken);

    private async Task<UiCapabilityResult<T>> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            try
            {
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    return Failure<T>(response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound => UiCapabilityResultCode.Denied,
                        HttpStatusCode.Conflict => UiCapabilityResultCode.Conflict,
                        HttpStatusCode.BadRequest => UiCapabilityResultCode.InvalidRequest,
                        _ => UiCapabilityResultCode.Unavailable
                    });
                return await response.Content.ReadFromJsonAsync<UiCapabilityResult<T>>(cancellationToken: ct)
                    ?? Failure<T>(UiCapabilityResultCode.Unavailable);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
            { return Failure<T>(UiCapabilityResultCode.Unavailable); }
        }
    }

    private static UiCapabilityResult<T> Failure<T>(UiCapabilityResultCode code) => new(code, default,
        UiCapabilityResults.UnavailableMetadata("case-discussion", "Discussion could not be loaded. Please retry."));
}