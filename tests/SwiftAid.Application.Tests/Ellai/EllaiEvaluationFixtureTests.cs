using System.Text.Json;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T21")]
public sealed class EllaiEvaluationFixtureTests
{
    private static string LoadFixture(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(new[] { d.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Fixture {string.Join('/', parts)} not found above test binary.");
    }

    [Fact]
    public void AudioFocusIsSingleOwnerWithSelfEchoGuard()
    {
        var focus = new AudioFocusCoordinator();
        Assert.True(focus.TryAcquire(AudioChannel.EllaiCapture));
        Assert.False(focus.TryAcquire(AudioChannel.CaseVoiceNote));
        focus.Release(AudioChannel.EllaiCapture);
        Assert.True(focus.TryAcquire(AudioChannel.CaseVoiceNote));

        focus.SetMuted(true);
        Assert.False(focus.CanPlayNarration());
        focus.SetMuted(false);

        Assert.True(AudioFocusCoordinator.IsSelfEcho(frameTaggedAsTtsOutput: true));
        Assert.False(AudioFocusCoordinator.IsSelfEcho(frameTaggedAsTtsOutput: false));
    }

    [Fact]
    public void DevelopmentDialogueFixturesAreWellFormedAndSafe()
    {
        var json = LoadFixture("fixtures", "ellai", "dialogues", "development", "core-turns.json");
        using var doc = JsonDocument.Parse(json);
        var turns = doc.RootElement.GetProperty("turns").EnumerateArray().ToList();

        Assert.True(turns.Count >= 3);
        foreach (var turn in turns)
        {
            Assert.True(turn.TryGetProperty("id", out _));
            Assert.True(turn.TryGetProperty("user", out _));
            Assert.True(turn.TryGetProperty("expectedKind", out _));
        }

        var text = json.ToLowerInvariant();
        Assert.DoesNotContain("sk-", text, StringComparison.Ordinal);
        Assert.Contains("mustRefuseClinical", json, StringComparison.Ordinal);
        Assert.Contains("mustKeepApprovalBoundary", json, StringComparison.Ordinal);
    }
}
