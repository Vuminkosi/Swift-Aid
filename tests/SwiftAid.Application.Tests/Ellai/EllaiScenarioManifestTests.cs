using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T16")]
public sealed class EllaiScenarioManifestTests
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
    public void DemoScenarioManifestLoadsWithFiniteBoundedScope()
    {
        var json = LoadFixture("fixtures", "ellai", "scenarios", "overdue-queue-review.json");

        var (manifest, error) = ScenarioManifestLoader.Load(json);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.True(manifest.Synthetic);
        Assert.InRange(manifest.TotalCycles, 1, 12);
        Assert.DoesNotContain(manifest.AllowedCapabilities, c => c.Contains("clinical", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClinicalManifestIsRejectedAtLoad()
    {
        var (manifest, error) = ScenarioManifestLoader.Load(
            """{"manifestId":"m","purpose":"p","scenarioId":"s","synthetic":true,"allowedCapabilities":["cases.read"],"totalCycles":4,"label":"triage outbreak demo"}""");

        Assert.Null(manifest);
        Assert.NotNull(error);
    }

    [Fact]
    public void UnboundedManifestIsRejectedAtLoad()
    {
        var (manifest, error) = ScenarioManifestLoader.Load(
            """{"manifestId":"m","purpose":"p","scenarioId":"s","synthetic":true,"allowedCapabilities":[],"totalCycles":99}""");

        Assert.Null(manifest);
        Assert.NotNull(error);
    }
}
