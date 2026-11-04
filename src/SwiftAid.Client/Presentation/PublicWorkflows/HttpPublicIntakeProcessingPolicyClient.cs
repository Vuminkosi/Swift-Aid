using System.Text.Json;

namespace SwiftAid.Client.Presentation.PublicWorkflows;

/// <summary>Reads the current public report-handling descriptor from the Gateway.</summary>
public sealed class HttpPublicIntakeProcessingPolicyClient(HttpClient client) : IPublicIntakeProcessingPolicyClient
{
    private const string Capability = "public-intake-processing-policy";
    private const int MaximumResponseBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<UiCapabilityResult<PublicReportProcessingPolicy>> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (client.BaseAddress is null) return UiCapabilityResults.Unavailable<PublicReportProcessingPolicy>(Capability);
        try
        {
            using var response = await client.GetAsync("public/v1/intake/processing-policy", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaximumResponseBytes ||
                response.Content.Headers.ContentType?.MediaType is not "application/json")
                return UiCapabilityResults.Unavailable<PublicReportProcessingPolicy>(Capability);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumResponseBytes) return UiCapabilityResults.Unavailable<PublicReportProcessingPolicy>(Capability);
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            var policy = JsonSerializer.Deserialize<PublicReportProcessingPolicy>(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), JsonOptions);
            return policy is { IsValid: true }
                ? new(UiCapabilityResultCode.Success, policy,
                    new(Capability, UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true))
                : UiCapabilityResults.Unavailable<PublicReportProcessingPolicy>(Capability);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return UiCapabilityResults.Unavailable<PublicReportProcessingPolicy>(Capability); }
    }
}
