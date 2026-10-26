using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests;

public sealed class MetaConfigurationTests
{
    [Fact]
    public async Task MissingCredentialDisablesProviderCapabilities()
    {
        var probe = new MetaCapabilityProbe(new MetaOptions
        {
            Provider = "meta",
            Route = InferenceRouteKind.MetaHostedApi,
            RouteAlias = "hosted-default",
            ModelId = "source-listed-model"
        }, SafeRegistry());

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CapabilityState.NotProvisioned, result.Text.State);
        Assert.False(result.Text.IsUsable);
        Assert.Equal(CapabilityState.NotProvisioned, result.Asr.State);
    }

    [Fact]
    public async Task ConfiguredCredentialDoesNotClaimProviderVerification()
    {
        const string credential = "test-secret-must-not-be-in-result";
        var probe = new MetaCapabilityProbe(new MetaOptions
        {
            Provider = "meta",
            Route = InferenceRouteKind.MetaHostedApi,
            RouteAlias = "hosted-default",
            CredentialConfigured = true,
            ModelId = "source-listed-model"
        }, SafeRegistry());

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CapabilityState.ConfiguredUnverified, result.Text.State);
        Assert.False(result.Text.IsUsable);
        Assert.DoesNotContain(credential, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NonMetaProviderIsRejected()
    {
        var options = new MetaOptions
        {
            Provider = "unapproved-provider",
            Route = InferenceRouteKind.MetaHostedApi,
        };

        Assert.Throws<MetaConfigurationException>(() => MetaOptionsValidator.Validate(options));
    }

    [Fact]
    public async Task MissingAsrDoesNotChangeTextCapability()
    {
        var probe = new MetaCapabilityProbe(new MetaOptions
        {
            Provider = "meta",
            Route = InferenceRouteKind.MetaHostedApi,
            RouteAlias = "hosted-default",
            CredentialConfigured = true,
            ModelId = "source-listed-model"
        }, SafeRegistry());

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CapabilityState.ConfiguredUnverified, result.Text.State);
        Assert.Equal(CapabilityState.NotProvisioned, result.Asr.State);
        Assert.Equal(CapabilityState.NotProvisioned, result.Pricing.State);
    }

    [Fact]
    public async Task EndpointFromUnverifiedAuthorityIsRejectedWithoutRequestingIt()
    {
        var probe = new MetaCapabilityProbe(new MetaOptions
        {
            Provider = "meta",
            Route = InferenceRouteKind.MetaHostedApi,
            RouteAlias = "hosted-default",
            ModelId = "source-listed-model",
            CredentialConfigured = true
        }, new TestEndpointRegistry(new MetaEndpointRegistration(
            Route: InferenceRouteKind.MetaHostedApi,
            RouteAlias: "hosted-default",
            ModelId: "source-listed-model",
            Endpoint: new Uri("https://192.0.2.10"),
            ExpectedAuthority: "192.0.2.10",
            EvidenceReference: "synthetic-test-source",
            PublicAuthorityVerified: true,
            AsrModelId: null,
            ToolsListed: false,
            AudioFormatsListed: [],
            LanguagesListed: [])));

        await Assert.ThrowsAsync<MetaConfigurationException>(async () =>
            await probe.ProbeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RegistrationForDifferentRouteKindIsRejected()
    {
        var probe = new MetaCapabilityProbe(new MetaOptions
        {
            Provider = "meta",
            Route = InferenceRouteKind.MetaHostedApi,
            RouteAlias = "hosted-default",
            ModelId = "source-listed-model",
            CredentialConfigured = true
        }, new TestEndpointRegistry(new MetaEndpointRegistration(
            Route: InferenceRouteKind.MetaSelfHostedOpenWeight,
            RouteAlias: "hosted-default",
            ModelId: "source-listed-model",
            Endpoint: new Uri("https://api.example.com/v1"),
            ExpectedAuthority: "api.example.com",
            EvidenceReference: "synthetic-test-source",
            PublicAuthorityVerified: true,
            AsrModelId: null,
            ToolsListed: false,
            AudioFormatsListed: [],
            LanguagesListed: [])));

        await Assert.ThrowsAsync<MetaConfigurationException>(async () =>
            await probe.ProbeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DefaultEmptyRegistryLeavesRouteNotProvisioned()
    {
        var probe = new MetaCapabilityProbe(new MetaOptions
        {
            Provider = "meta",
            Route = InferenceRouteKind.MetaHostedApi,
            RouteAlias = "unlisted-alias",
            ModelId = "unlisted-model",
            CredentialConfigured = true
        });

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CapabilityState.NotProvisioned, result.Text.State);
        Assert.False(result.Text.IsUsable);
    }

    [Fact]
    public async Task EndpointAuthorityMustMatchItsRegistryRecord()
    {
        var registration = new MetaEndpointRegistration(
            Route: InferenceRouteKind.MetaHostedApi,
            RouteAlias: "hosted-default",
            ModelId: "source-listed-model",
            Endpoint: new Uri("https://api.example.com/v1"),
            ExpectedAuthority: "different.example.com",
            EvidenceReference: "synthetic-test-source",
            PublicAuthorityVerified: true,
            AsrModelId: null,
            ToolsListed: false,
            AudioFormatsListed: [],
            LanguagesListed: []);
        var probe = new MetaCapabilityProbe(new MetaOptions
        {
            Provider = "meta",
            Route = InferenceRouteKind.MetaHostedApi,
            RouteAlias = "hosted-default",
            ModelId = "source-listed-model",
            CredentialConfigured = true
        }, new TestEndpointRegistry(registration));

        await Assert.ThrowsAsync<MetaConfigurationException>(async () =>
            await probe.ProbeAsync(CancellationToken.None));
    }

    private static IMetaEndpointRegistry SafeRegistry() => new TestEndpointRegistry(new MetaEndpointRegistration(
        Route: InferenceRouteKind.MetaHostedApi,
        RouteAlias: "hosted-default",
        ModelId: "source-listed-model",
        Endpoint: new Uri("https://api.example.com/v1"),
        ExpectedAuthority: "api.example.com",
        EvidenceReference: "synthetic-test-source",
        PublicAuthorityVerified: true,
        AsrModelId: null,
        ToolsListed: false,
        AudioFormatsListed: [],
        LanguagesListed: []));

    private sealed class TestEndpointRegistry(MetaEndpointRegistration registration) : IMetaEndpointRegistry
    {
        public ValueTask<MetaEndpointRegistration?> FindAsync(
            string routeAlias,
            string modelId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<MetaEndpointRegistration?>(
                registration.RouteAlias == routeAlias && registration.ModelId == modelId
                    ? registration
                    : null);
        }
    }
}
