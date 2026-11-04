using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;

namespace SwiftAid.Browser.Services;

/// <summary>Reads only the allowlisted public policy bootstrap on the browser side.</summary>
public sealed class BrowserPublicIntakeProcessingPolicyClient(
    IJSRuntime jsRuntime,
    NavigationManager navigation) : IPublicIntakeProcessingPolicyClient
{
    private readonly IJSRuntime _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
    private readonly NavigationManager _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));

    public async Task<UiCapabilityResult<PublicReportProcessingPolicy>> GetPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IJSObjectReference? module = null;
        try
        {
            var moduleUri = _navigation.ToAbsoluteUri("js/public-policy-bootstrap.js");
            module = await _jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken, moduleUri);
            var snapshot = await module.InvokeAsync<PublicPolicySnapshot?>("readPublicPolicy", cancellationToken);
            if (snapshot is null || !snapshot.IsAvailable ||
                string.IsNullOrWhiteSpace(snapshot.Reference) ||
                string.IsNullOrWhiteSpace(snapshot.Version))
            {
                return UiCapabilityResults.Unavailable<PublicReportProcessingPolicy>("public-intake-processing-policy");
            }

            var provenance = Enum.TryParse<UiCapabilityProvenance>(snapshot.Provenance, ignoreCase: true, out var parsed)
                ? parsed
                : UiCapabilityProvenance.LiveService;
            var policy = new PublicReportProcessingPolicy(
                snapshot.Reference,
                snapshot.Version,
                snapshot.NoticeText,
                snapshot.AcknowledgementLabel);
            var metadata = new UiCapabilityMetadata(
                "public-intake-processing-policy",
                UiCapabilityResults.ContractVersion,
                provenance,
                snapshot.UpdatedAtUtc ?? DateTimeOffset.UtcNow,
                IsAvailable: true,
                snapshot.MetadataNotice);
            return new UiCapabilityResult<PublicReportProcessingPolicy>(
                UiCapabilityResultCode.Success,
                policy,
                metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UiCapabilityResults.Unavailable<PublicReportProcessingPolicy>("public-intake-processing-policy");
        }
        finally
        {
            if (module is not null)
            {
                try
                {
                    await module.DisposeAsync();
                }
                catch (Exception)
                {
                    // The policy result remains valid if module cleanup is interrupted.
                }
            }
        }
    }

    private sealed record PublicPolicySnapshot(
        string? Reference,
        string? Version,
        string? NoticeText,
        string? AcknowledgementLabel,
        string? Provenance,
        string? MetadataNotice,
        DateTimeOffset? UpdatedAtUtc,
        bool IsAvailable);
}
