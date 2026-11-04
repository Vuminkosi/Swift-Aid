using System.Text.Json.Nodes;
using SwiftAid.Contracts.Intake;
using SwiftAid.UI.Components.Public;

namespace SwiftAid.Ui.Tests;

public sealed class PublicReportSnapshotVersionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void CurrentSnapshotPreservesDeathRelated(bool? deathRelated)
    {
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human, DeathRelated = deathRelated };
        var snapshot = draft.ToFormSnapshot();

        Assert.Equal(7, snapshot.SchemaVersion);
        Assert.Equal(deathRelated, PublicReportDraft.FromFormSnapshot(snapshot).DeathRelated);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void PreviousSnapshotsRestoreWithUnknownDeathRelated(int version)
    {
        var json = JsonNode.Parse(new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human }
            .ToFormSnapshot().Json)!.AsObject();
        json.Remove(nameof(PublicReportDraft.DeathRelated));
        if (version < 6) json.Remove(nameof(PublicReportDraft.OnsetDateInterpretedFromCaptureContext));
        if (version < 5)
        {
            foreach (var name in new[] { nameof(PublicReportDraft.OnsetPrecision), nameof(PublicReportDraft.OnsetDate),
                nameof(PublicReportDraft.OnsetDateText), nameof(PublicReportDraft.OnsetTime),
                nameof(PublicReportDraft.OnsetOffset), nameof(PublicReportDraft.OnsetZoneId) }) json.Remove(name);
        }
        if (version < 4) json.Remove(nameof(PublicReportDraft.Gender));
        if (version < 3) json.Remove(nameof(PublicReportDraft.AgeYearsText));
        if (version < 2) json.Remove(nameof(PublicReportDraft.OccurrenceDateText));

        var restored = PublicReportDraft.FromFormSnapshot(new(version, json.ToJsonString()));
        Assert.Null(restored.DeathRelated);
        Assert.Equal(7, restored.ToFormSnapshot().SchemaVersion);
    }

    [Fact]
    public void PreviouslyWrittenVersionSixSnapshotPreservesDeathRelated()
    {
        var snapshot = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human, DeathRelated = true }.ToFormSnapshot();
        var restored = PublicReportDraft.FromFormSnapshot(new(6, snapshot.Json));
        Assert.True(restored.DeathRelated);
    }

    [Fact]
    public void CurrentSnapshotRejectsMissingDeathRelatedField()
    {
        var snapshot = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human }.ToFormSnapshot();
        var json = JsonNode.Parse(snapshot.Json)!.AsObject();
        json.Remove(nameof(PublicReportDraft.DeathRelated));
        Assert.Throws<InvalidOperationException>(() => PublicReportDraft.FromFormSnapshot(new(7, json.ToJsonString())));
    }
}
