using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwiftAid.Domain.Tests;

/// <summary>
/// Pins the public intake JSON Schemas to the backend <c>IntakeSubmissionRules</c> contract.
/// The schemas are client-side guidance only; the server re-validates everything. These tests
/// pin the structural markers (branches, const affirmations, patterns) and the headline
/// accept/reject behavior of each pattern so contract drift fails fast in CI.
/// Full semantic validation (cross-object equality, tz-database checks, range ordering)
/// remains server-side and is covered by the Domain rule tests.
/// </summary>
public sealed class IntakeSchemaContractTests : IDisposable
{
    private readonly string _schemaDirectory;
    private readonly JsonDocument _submission;
    private readonly JsonDocument _metadata;
    private readonly JsonDocument _catalogue;
    private readonly JsonDocument _sessionRequest;

    public IntakeSchemaContractTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "contracts", "intake")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "The contracts/intake directory must be reachable from the test output.");
        _schemaDirectory = Path.Combine(directory!.FullName, "contracts", "intake");
        _submission = Load("intake-submission.v1.schema.json");
        _metadata = Load("intake-metadata.v1.schema.json");
        _catalogue = Load("intake-country-catalogue.v1.schema.json");
        _sessionRequest = Load("intake-session-request.v1.schema.json");
    }

    [Fact]
    public void SubmissionSchema_RequiresNonEmptyReportContent()
    {
        var branches = _submission.RootElement.GetProperty("allOf")[0].GetProperty("anyOf");
        Assert.Equal(3, branches.GetArrayLength());
    }

    [Fact]
    public void SubmissionSchema_AllowsOptionalNonnegativeHumanAge()
    {
        var age = _submission.RootElement.GetProperty("properties").GetProperty("ageYears");
        Assert.Equal("integer", age.GetProperty("type").GetString());
        Assert.Equal(0, age.GetProperty("minimum").GetInt32());
        Assert.DoesNotContain(_submission.RootElement.GetProperty("required").EnumerateArray(), value => value.GetString() == "ageYears");
        var rule = _submission.RootElement.GetProperty("allOf")[2];
        Assert.Equal("ageYears", rule.GetProperty("if").GetProperty("required")[0].GetString());
        Assert.Equal("human", rule.GetProperty("then").GetProperty("properties").GetProperty("subjectScope").GetProperty("const").GetString());
    }

    [Fact]
    public void SubmissionSchema_RejectsNilSubmissionAndAudioIds()
    {
        var pattern = Pattern(_submission, "nonNilUuid");
        Assert.Matches(new Regex(pattern), "3f2504e0-4f89-11d3-9a0c-0305e82c3301");
        Assert.DoesNotMatch(new Regex(pattern), "00000000-0000-0000-0000-000000000000");
        Assert.Equal("#/$defs/nonNilUuid", Ref(_submission, "clientSubmissionId"));
        Assert.Equal("#/$defs/nonNilUuid", ItemsRef(_submission, "originalAudioAttachmentIds"));
    }

    [Fact]
    public void SubmissionSchema_EnforcesTrimmedText()
    {
        var narrative = new Regex(PatternAt(_submission.RootElement, "properties", "narrative", "pattern"));
        Assert.Matches(narrative, "Caller saw the dog.");
        Assert.Matches(narrative, string.Empty);
        Assert.DoesNotMatch(narrative, " leading");
        Assert.DoesNotMatch(narrative, "trailing ");
        Assert.DoesNotMatch(narrative, "   ");

        var observation = new Regex(PatternAt(_submission.RootElement, "properties", "observations", "items", "pattern"));
        Assert.Matches(observation, "Fever");
        Assert.DoesNotMatch(observation, " ");
        Assert.DoesNotMatch(observation, " x");
    }

    [Fact]
    public void SubmissionSchema_RequiresAffirmativeConfirmations()
    {
        var confirmations = _submission.RootElement.GetProperty("properties").GetProperty("confirmations").GetProperty("properties");
        foreach (var name in new[] { "reportReviewConfirmed", "occurrenceCountryConfirmed", "processingConfirmed" })
        {
            Assert.True(confirmations.GetProperty(name).GetProperty("const").GetBoolean());
        }
    }

    [Fact]
    public void SubmissionSchema_CouplesRecordingConsentToAudio()
    {
        var coupling = _submission.RootElement.GetProperty("allOf")[1];
        var withAudio = coupling.GetProperty("then").GetProperty("properties").GetProperty("confirmations").GetProperty("properties");
        Assert.True(withAudio.GetProperty("recordingConfirmed").GetProperty("const").GetBoolean());

        var withoutAudio = coupling.GetProperty("else").GetProperty("properties").GetProperty("confirmations").GetProperty("properties");
        foreach (var name in new[] { "recordingConfirmed", "recordingPolicyReference", "recordingPolicyVersion" })
        {
            Assert.Equal(JsonValueKind.Null, withoutAudio.GetProperty(name).GetProperty("const").ValueKind);
        }
    }

    [Fact]
    public void SubmissionSchema_ReportedTimeBranchesMatchPrecision()
    {
        var branches = _submission.RootElement.GetProperty("$defs").GetProperty("reportedTime").GetProperty("allOf");
        Assert.Equal(4, branches.GetArrayLength());

        var approximate = branches.EnumerateArray()
            .First(branch => branch.GetProperty("if").GetProperty("properties").GetProperty("precision").GetProperty("const").GetString() == "approximate");
        Assert.Equal(3, approximate.GetProperty("then").GetProperty("oneOf").GetArrayLength());
    }

    [Fact]
    public void SubmissionSchema_OffsetPatternMatchesBackendBounds()
    {
        var offset = new Regex(PatternAt(_submission.RootElement, "$defs", "reportedTime", "properties", "originalOffset", "pattern"));
        Assert.Matches(offset, "+05:30");
        Assert.Matches(offset, "-14:00");
        Assert.Matches(offset, "+14:00");
        Assert.DoesNotMatch(offset, "+14:30");
        Assert.DoesNotMatch(offset, "+15:00");
        Assert.DoesNotMatch(offset, "05:30");
    }

    [Fact]
    public void MetadataAndCatalogue_VersionsUseBackendCharset()
    {
        foreach (var property in new[] { "version", "countryCatalogueVersion", "privacyNoticeVersion" })
        {
            var pattern = new Regex(PatternAt(_metadata.RootElement, "properties", property, "pattern"));
            Assert.Matches(pattern, "2026.10.27-1");
            Assert.DoesNotMatch(pattern, "v 1");
        }

        Assert.Equal(1, _catalogue.RootElement.GetProperty("properties").GetProperty("countries").GetProperty("minItems").GetInt32());
    }

    [Fact]
    public void SessionRequestSchema_PinsSessionBoundaries()
    {
        var properties = _sessionRequest.RootElement.GetProperty("properties");
        Assert.Equal("uuid", properties.GetProperty("sessionId").GetProperty("format").GetString());
        var proof = new Regex(PatternAt(properties, "receiptProof", "pattern"));
        Assert.Matches(proof, new string('A', 43));
        Assert.DoesNotMatch(proof, new string('A', 42));
    }

    public void Dispose()
    {
        _submission.Dispose();
        _metadata.Dispose();
        _catalogue.Dispose();
        _sessionRequest.Dispose();
    }

    private JsonDocument Load(string fileName)
    {
        using var stream = File.OpenRead(Path.Combine(_schemaDirectory, fileName));
        return JsonDocument.Parse(stream);
    }

    private static string Pattern(JsonDocument document, string definition) =>
        PatternAt(document.RootElement, "$defs", definition, "pattern");

    private static string PatternAt(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            element = element.GetProperty(segment);
        }

        return element.GetString() ?? throw new InvalidOperationException("Schema pattern must be a string.");
    }

    private static string Ref(JsonDocument document, string property) =>
        document.RootElement.GetProperty("properties").GetProperty(property).GetProperty("$ref").GetString()!;

    private static string ItemsRef(JsonDocument document, string property) =>
        document.RootElement.GetProperty("properties").GetProperty(property).GetProperty("items").GetProperty("$ref").GetString()!;
}
