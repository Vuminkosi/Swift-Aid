using SwiftAid.Application.Ellai;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Geography;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiIngestionToolTests
{
    private static readonly EllaiToolContext Context = new("session", "epoch", "public.product-help", null, 4, true,
        DataClassification: EllaiProcessingClassification.UserProvidedNonSensitive,
        ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose);

    [Fact]
    public async Task Schema_is_available_to_guest_but_does_not_claim_submission()
    {
        var result = await Broker().InvokeAsync(Context, new(EllaiToolNames.IngestionSchema, new Dictionary<string, string>()), default);
        Assert.True(result.Allowed);
        Assert.Contains("observations_json", result.ResultSummary);
        Assert.Contains("durable receipt", result.ResultSummary);
        Assert.NotEmpty(result.Evidence);
    }

    [Fact]
    public async Task Validation_accepts_original_strings_beyond_reference_length_without_echoing_them()
    {
        var text = new string('x', 200);
        var result = await Broker().InvokeAsync(Context, new(EllaiToolNames.ValidateObservations,
            new Dictionary<string, string> { ["observations_json"] = $"[\"{text}\"]" }), default);
        Assert.True(result.Allowed);
        Assert.DoesNotContain(text, result.ResultSummary);
        Assert.Contains("Nothing has been submitted", result.ResultSummary);
    }

    [Theory]
    [InlineData("[{\"diagnosis\":\"invented\"}]")]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    public async Task Invalid_observation_shapes_are_denied(string json)
    {
        var result = await Broker().InvokeAsync(Context, new(EllaiToolNames.ValidateObservations,
            new Dictionary<string, string> { ["observations_json"] = json }), default);
        Assert.False(result.Allowed);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, result.ReasonCode);
    }

    [Fact]
    public async Task Location_results_retain_registry_provenance_and_require_selection()
    {
        var result = await Broker().InvokeAsync(Context, new(EllaiToolNames.Locations,
            new Dictionary<string, string> { ["q"] = "Rusape", ["country"] = "ZW" }), default);
        Assert.True(result.Allowed);
        Assert.Equal("registry-test/1", Assert.Single(result.Evidence).Version);
        Assert.Contains("requiresUserSelection", result.ResultSummary);
    }

    [Fact]
    public async Task Budget_scope_and_write_boundaries_apply_to_ingestion_tools()
    {
        var broker = Broker();
        Assert.False((await broker.InvokeAsync(Context with { RemainingCalls = 0 }, new(EllaiToolNames.IngestionSchema, new Dictionary<string, string>()), default)).Allowed);
        Assert.False((await broker.InvokeAsync(Context with { ScopeEpoch = "" }, new(EllaiToolNames.IngestionSchema, new Dictionary<string, string>()), default)).Allowed);
        Assert.False((await broker.InvokeAsync(Context, new("ellai_create_case", new Dictionary<string, string>()), default)).Allowed);
    }

    private static EllaiToolBroker Broker() => new(new EllaiPolicyService(), new EllaiConsentService(), geography: new Registry());

    internal sealed class Registry : IGeographicRegistry
    {
        public Task<GeoSearchResponse> SearchAsync(string query, string? countryCode, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new GeoSearchResponse("registry-test/1", 1,
                [new GeoSearchResult("ZW:place:rusape", "Rusape", GeoLocationKind.Settlement, "ZW", "Zimbabwe", "Manicaland", null, null, false, [])]));
        public Task<GeoChildrenResponse> GetChildrenAsync(string parentId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
