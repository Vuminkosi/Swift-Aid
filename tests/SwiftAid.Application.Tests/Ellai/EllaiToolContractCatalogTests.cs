using System.Text.Json;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "SW-01-004")]
public sealed class EllaiToolContractCatalogTests
{
    [Fact]
    public void EveryAdvertisedToolHasAClosedVersionedSchema()
    {
        Assert.Equal(EllaiToolNames.All.Order(StringComparer.Ordinal),
            EllaiToolContractCatalog.All.Keys.Order(StringComparer.Ordinal));

        foreach (var tool in EllaiToolNames.All)
        {
            using var schema = JsonDocument.Parse(EllaiToolContractCatalog.ToJsonSchema(tool));
            var root = schema.RootElement;
            Assert.Equal("https://json-schema.org/draft/2020-12/schema",
                root.GetProperty("$schema").GetString());
            Assert.Equal(EllaiToolContractCatalog.CurrentSchemaVersion,
                root.GetProperty("x-swiftaid-schema-version").GetInt32());
            Assert.Equal("object", root.GetProperty("type").GetString());
            Assert.False(root.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(EllaiToolContractCatalog.All[tool].Arguments.Count,
                root.GetProperty("properties").EnumerateObject().Count());
        }
    }

    [Fact]
    public void RequiredUnknownAndOversizedArgumentsAreRejected()
    {
        Assert.False(EllaiToolContractCatalog.TryValidateArguments(
            EllaiToolNames.DocsSearch, new Dictionary<string, string>(), out var missing));
        Assert.Contains("q", missing, StringComparison.Ordinal);

        Assert.False(EllaiToolContractCatalog.TryValidateArguments(
            EllaiToolNames.Now, new Dictionary<string, string> { ["query"] = "x" }, out var unknown));
        Assert.Contains("query", unknown, StringComparison.Ordinal);

        Assert.False(EllaiToolContractCatalog.TryValidateArguments(
            EllaiToolNames.State, new Dictionary<string, string> { ["ref"] = "ignored" }, out _));

        Assert.False(EllaiToolContractCatalog.TryValidateArguments(
            EllaiToolNames.Evidence, new Dictionary<string, string> { ["ref"] = "arbitrary" }, out _));

        Assert.False(EllaiToolContractCatalog.TryValidateArguments(
            EllaiToolNames.State, new Dictionary<string, string> { ["projection"] = new string('x', 121) }, out _));

        Assert.True(EllaiToolContractCatalog.TryValidateArguments(
            EllaiToolNames.ValidateObservations,
            new Dictionary<string, string> { ["observations_json"] = new string('x', 16_384) }, out _));
    }

    [Fact]
    public void ResultSchemaMatchesTheClosedVersionedWireEnvelope()
    {
        using var schema = JsonDocument.Parse(EllaiToolContractCatalog.ToResultJsonSchema());
        var root = schema.RootElement;
        Assert.Equal("EllaiToolResult", root.GetProperty("title").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(8, root.GetProperty("required").GetArrayLength());
        Assert.Equal(5, root.GetProperty("properties").GetProperty("evidence")
            .GetProperty("items").GetProperty("required").GetArrayLength());
    }

    [Fact]
    public void EvidenceSchemaConstrainsReferenceAndFieldSelectors()
    {
        using var schema = JsonDocument.Parse(
            EllaiToolContractCatalog.ToJsonSchema(EllaiToolNames.Evidence));
        var properties = schema.RootElement.GetProperty("properties");
        Assert.Equal(new[] { "case-results", "case-results-original" }, properties.GetProperty("ref")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal(new[] { "country", "workflow", "classification" },
            properties.GetProperty("field").GetProperty("enum").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[] { "Classified", "Unclassified" }, properties.GetProperty("classification_filter")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray());
    }

    [Fact]
    public void SchemasDescribeTheHandlerEnumCountryAndLimitBoundaries()
    {
        using var schema = JsonDocument.Parse(EllaiToolContractCatalog.ToJsonSchema(EllaiToolNames.State));
        var properties = schema.RootElement.GetProperty("properties");

        Assert.Equal(new[] { "feature", "queue" },
            properties.GetProperty("projection").GetProperty("enum").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[] { "Reported", "UnderReview", "Active", "AwaitingFollowUp", "Resolved" },
            properties.GetProperty("workflow").GetProperty("enum").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        Assert.Equal("^[A-Z]{2}$", properties.GetProperty("country").GetProperty("pattern").GetString());
        Assert.Equal("^([1-9]|1[0-9]|20)$", properties.GetProperty("limit").GetProperty("pattern").GetString());
        Assert.Equal(1, properties.GetProperty("projection").GetProperty("minLength").GetInt32());
    }

    [Theory]
    [InlineData("workflow", "Unknown", EllaiToolNames.State)]
    [InlineData("sort", "oldest", EllaiToolNames.State)]
    [InlineData("country", "sz", EllaiToolNames.State)]
    [InlineData("limit", "0", EllaiToolNames.State)]
    [InlineData("limit", "21", EllaiToolNames.UiCatalogue)]
    [InlineData("limit", "11", EllaiToolNames.Locations)]
    public void HandlerConstraintsAreEnforcedAtTheSharedContractBoundary(
        string argumentName, string value, string toolName)
    {
        Assert.False(EllaiToolContractCatalog.TryValidateArguments(
            toolName, new Dictionary<string, string> { [argumentName] = value }, out _));
    }

    [Fact]
    public void RequiredStringArgumentsCannotBeEmpty()
    {
        Assert.False(EllaiToolContractCatalog.TryValidateArguments(
            EllaiToolNames.DocsSearch, new Dictionary<string, string> { ["q"] = string.Empty }, out _));
    }

    [Fact]
    public void ParserRejectsArgumentsOutsideTheRegisteredSchema()
    {
        var parsed = EllaiModelTurnParser.Parse(
            """{"tool_calls":[{"id":"c1","name":"ellai_now","args":{"query":"private"}}]}""");

        Assert.Null(parsed.Turn);
        Assert.Contains("not declared", parsed.Error, StringComparison.Ordinal);

        var invalidEnum = EllaiModelTurnParser.Parse(
            """{"tool_calls":[{"id":"c2","name":"ellai_state","args":{"projection":"unknown"}}]}""");
        Assert.Null(invalidEnum.Turn);
        Assert.Contains("declared string format", invalidEnum.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelCataloguePublishesOnlyAllowedInputSchemasAndSharedResultSchema()
    {
        var catalogue = EllaiToolBroker.DescribeCatalogue(
            new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.ProductFacts });

        Assert.Contains("ellai_productfacts", catalogue, StringComparison.Ordinal);
        Assert.Contains("Input JSON Schema v1", catalogue, StringComparison.Ordinal);
        Assert.Contains("Output JSON Schema", catalogue, StringComparison.Ordinal);
        Assert.DoesNotContain("ellai_state(", catalogue, StringComparison.Ordinal);
        Assert.DoesNotContain("ellai_evidence(", catalogue, StringComparison.Ordinal);
    }
}
