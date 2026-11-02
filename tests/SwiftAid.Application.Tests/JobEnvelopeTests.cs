using System.Text.Json;
using System.Text;
using SwiftAid.Contracts.Jobs;

namespace SwiftAid.Application.Tests;

public sealed class JobEnvelopeTests
{
    [Fact]
    public void SerializeWritesExactlyTheVersionOneEnvelopeFields()
    {
        var payload = CreateEnvelope().SerializeToUtf8();
        using var document = JsonDocument.Parse(payload);

        var properties = document.RootElement.EnumerateObject().ToArray();

        Assert.Equal(
            ["schemaVersion", "messageId", "jobId", "jobType", "organizationId", "inputSnapshotId", "correlationId", "createdAtUtc"],
            properties.Select(property => property.Name));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("CaseSummary", document.RootElement.GetProperty("jobType").GetString());
    }

    [Fact]
    public void DeserializeRejectsUnknownPrivatePayloadFields()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            messageId = Guid.NewGuid(),
            jobId = Guid.NewGuid(),
            jobType = "CaseSummary",
            organizationId = "demo-zw",
            inputSnapshotId = Guid.NewGuid(),
            correlationId = Guid.NewGuid(),
            createdAtUtc = "2026-10-25T07:00:00Z",
            patientNarrative = "must not be sent"
        });

        Assert.Throws<JsonException>(() => JobEnvelope.DeserializeFromUtf8(payload));
    }

    [Fact]
    public void DeserializeRejectsWrongCaseFieldNames()
    {
        var payload = Encoding.UTF8.GetBytes(
            "{\"SchemaVersion\":1,\"messageId\":\"9160e07e-1385-4b30-995b-eae5fd2651db\",\"jobId\":\"a8671879-8132-4f46-a689-628fe3791d57\",\"jobType\":\"CaseSummary\",\"organizationId\":\"demo-zw\",\"inputSnapshotId\":\"d31a5e8b-0445-4134-894c-57519ce031b9\",\"correlationId\":\"081a93cf-1353-43b7-ae5d-dd045786a527\",\"createdAtUtc\":\"2026-10-25T07:00:00Z\"}");

        Assert.Throws<JsonException>(() => JobEnvelope.DeserializeFromUtf8(payload));
    }

    [Fact]
    public void DeserializeRejectsDuplicateFields()
    {
        var payload = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"messageId\":\"9160e07e-1385-4b30-995b-eae5fd2651db\",\"jobId\":\"a8671879-8132-4f46-a689-628fe3791d57\",\"jobType\":\"CaseSummary\",\"organizationId\":\"demo-zw\",\"inputSnapshotId\":\"d31a5e8b-0445-4134-894c-57519ce031b9\",\"correlationId\":\"081a93cf-1353-43b7-ae5d-dd045786a527\",\"createdAtUtc\":\"2026-10-25T07:00:00Z\",\"jobId\":\"a8671879-8132-4f46-a689-628fe3791d57\"}");

        Assert.Throws<JsonException>(() => JobEnvelope.DeserializeFromUtf8(payload));
    }

    [Theory]
    [InlineData("casesummary")]
    [InlineData("CaseSummary ")]
    [InlineData("Unknown")]
    public void DeserializeRejectsUnknownOrNonCanonicalJobTypes(string jobType)
    {
        var payload = Encoding.UTF8.GetBytes(
            $"{{\"schemaVersion\":1,\"messageId\":\"9160e07e-1385-4b30-995b-eae5fd2651db\",\"jobId\":\"a8671879-8132-4f46-a689-628fe3791d57\",\"jobType\":\"{jobType}\",\"organizationId\":\"demo-zw\",\"inputSnapshotId\":\"d31a5e8b-0445-4134-894c-57519ce031b9\",\"correlationId\":\"081a93cf-1353-43b7-ae5d-dd045786a527\",\"createdAtUtc\":\"2026-10-25T07:00:00Z\"}}");

        Assert.Throws<JsonException>(() => JobEnvelope.DeserializeFromUtf8(payload));
    }

    [Fact]
    public void DeserializeRejectsInvalidIdentifiers()
    {
        var payload = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"messageId\":\"00000000-0000-0000-0000-000000000000\",\"jobId\":\"a8671879-8132-4f46-a689-628fe3791d57\",\"jobType\":\"CaseSummary\",\"organizationId\":\"demo-zw\",\"inputSnapshotId\":\"d31a5e8b-0445-4134-894c-57519ce031b9\",\"correlationId\":\"081a93cf-1353-43b7-ae5d-dd045786a527\",\"createdAtUtc\":\"2026-10-25T07:00:00Z\"}");

        Assert.Throws<ArgumentException>(() => JobEnvelope.DeserializeFromUtf8(payload));
    }

    [Fact]
    public void DeserializeRequiresUtcTimestamp()
    {
        var payload = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"messageId\":\"9160e07e-1385-4b30-995b-eae5fd2651db\",\"jobId\":\"a8671879-8132-4f46-a689-628fe3791d57\",\"jobType\":\"CaseSummary\",\"organizationId\":\"demo-zw\",\"inputSnapshotId\":\"d31a5e8b-0445-4134-894c-57519ce031b9\",\"correlationId\":\"081a93cf-1353-43b7-ae5d-dd045786a527\",\"createdAtUtc\":\"2026-10-25T09:00:00+02:00\"}");

        Assert.Throws<ArgumentException>(() => JobEnvelope.DeserializeFromUtf8(payload));
    }

    [Fact]
    public void DeserializeRejectsOversizedPayloadBeforeParsing()
    {
        var payload = new byte[JobEnvelope.MaximumMessageBytes + 1];

        Assert.Throws<ArgumentException>(() => JobEnvelope.DeserializeFromUtf8(payload));
    }

    [Fact]
    public void SerializeRejectsOversizedOrganizationIdentifier()
    {
        var envelope = CreateEnvelope() with
        {
            OrganizationId = new string('x', JobEnvelope.MaximumMessageBytes)
        };

        Assert.Throws<ArgumentException>(() => envelope.SerializeToUtf8());
    }

    [Fact]
    public void OrganizationIdentifierValidationMatchesWorkerLimits()
    {
        var invalidValues = new[]
        {
            " ",
            " demo-zw",
            "demo-zw ",
            "demo\u001fzw",
            new string('x', 201),
            new string('x', 199) + "😀"
        };

        foreach (var organizationId in invalidValues)
        {
            Assert.Throws<ArgumentException>(() => (CreateEnvelope() with { OrganizationId = organizationId }).SerializeToUtf8());
        }

        var maximumLength = CreateEnvelope() with { OrganizationId = new string('x', 200) };
        Assert.Equal(new string('x', 200), JobEnvelope.DeserializeFromUtf8(maximumLength.SerializeToUtf8()).OrganizationId);
        var supplementaryId = string.Concat(Enumerable.Repeat("😀", 100));
        var supplementaryCharacters = CreateEnvelope() with { OrganizationId = supplementaryId };
        Assert.Equal(supplementaryId, JobEnvelope.DeserializeFromUtf8(supplementaryCharacters.SerializeToUtf8()).OrganizationId);
    }

    private static JobEnvelope CreateEnvelope() => new(
        JobEnvelope.CurrentSchemaVersion,
        Guid.Parse("9160e07e-1385-4b30-995b-eae5fd2651db"),
        Guid.Parse("a8671879-8132-4f46-a689-628fe3791d57"),
        JobType.CaseSummary,
        "demo-zw",
        Guid.Parse("d31a5e8b-0445-4134-894c-57519ce031b9"),
        Guid.Parse("081a93cf-1353-43b7-ae5d-dd045786a527"),
        new DateTimeOffset(2026, 10, 25, 7, 0, 0, TimeSpan.Zero));
}
