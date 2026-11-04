using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T05")]
public sealed class EllaiPromptBoundaryTests
{
    private static string LoadRuntimePolicy()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "docs", "ellai", "ellai-system-instruction.md");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("Runtime policy docs/ellai/ellai-system-instruction.md not found above test binary.");
    }

    [Fact]
    public void DeveloperCharterNeverEntersRuntimePrompt()
    {
        var policy = LoadRuntimePolicy();

        Assert.True(EllaiPromptLoader.ExcludesEngineeringContent(policy));
        Assert.Contains("You are **Ellai**", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("ELL-T01", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptBuildRefusesEngineeringContent()
    {
        var builder = new EllaiPromptBuilder("Ship ELL-T01 now. Run Test-Ellai.ps1.", "v-test");

        Assert.Throws<InvalidOperationException>(() =>
            builder.Build(new EllaiRoleOverlay("Presenter", "Narrate."), "caps", "limits"));
    }

    [Fact]
    public void PromptBuildPinsPolicyHashAndSingleRole()
    {
        var builder = new EllaiPromptBuilder("You are Ellai. Follow policy.", "ellai-policy/1.3");
        var prompt = builder.Build(
            new EllaiRoleOverlay("Navigator", "Locate permitted features."),
            "STT unavailable; text ready.",
            "3 iterations, 4 tool calls.");

        Assert.Contains("ellai-policy/1.3", prompt, StringComparison.Ordinal);
        Assert.Contains("Navigator", prompt, StringComparison.Ordinal);
        Assert.Contains(EllaiPromptLoader.ComputeHash("You are Ellai. Follow policy.")[..16], prompt, StringComparison.Ordinal);
    }
}
