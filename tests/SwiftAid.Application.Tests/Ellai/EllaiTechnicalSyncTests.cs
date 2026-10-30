using System.Text.RegularExpressions;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

/// <summary>Documentation-code sync gate (DOC-14 flavor): the approved
/// technical reference must name every registered tool, name no unknown
/// tool, and cite the same policy pin as the policy file. A route, tool or
/// policy change without a reference update fails here, not silently.</summary>
[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T07")]
public sealed class EllaiTechnicalSyncTests
{
    private static readonly IReadOnlySet<string> RetiredNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "ellai_voice_capability",
        "ellai_guidance_session",
        "ellai_caption",
        "ellai_guidance_anchor_catalogue",
    };

    /// <summary>Documented PostgreSQL tables, not tools. Any other
    /// unknown `ellai_*` token is a stale tool claim until allowlisted here
    /// with its meaning.</summary>
    private static readonly IReadOnlySet<string> DocumentedStores = new HashSet<string>(StringComparer.Ordinal)
    {
        "ellai_topics",
        "ellai_plan_approvals",
    };

    private static string LoadReference()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "docs", "TECHNICAL.md");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("docs/TECHNICAL.md not found above test binary.");
    }

    private static string LoadPolicy()
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
    public void ReferenceDocumentsEveryRegisteredTool()
    {
        var reference = LoadReference();

        foreach (var tool in EllaiToolNames.All)
        {
            Assert.Contains($"`{tool}`", reference, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReferenceNamesOnlyKnownTools()
    {
        var reference = LoadReference();
        var tokens = Regex.Matches(reference, "`(ellai_[a-z_]+)`").Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(tokens);
        foreach (var token in tokens.Distinct(StringComparer.Ordinal))
        {
            Assert.True(
                EllaiToolNames.All.Contains(token)
                    || RetiredNames.Contains(token)
                    || DocumentedStores.Contains(token),
                $"Reference names unknown tool '{token}'.");
        }
    }

    [Fact]
    public void ReferenceCitesCurrentPolicyPin()
    {
        var reference = LoadReference();
        var policy = LoadPolicy();

        var cited = Regex.Match(reference, @"currently version \*\*([0-9.]+)\*\*");
        var declared = Regex.Match(policy, @"Policy version: \*\*([0-9.]+)\*\*");

        Assert.True(cited.Success, "Reference must cite the current policy version.");
        Assert.True(declared.Success, "Policy file must declare its version.");
        Assert.Equal(declared.Groups[1].Value, cited.Groups[1].Value);
    }
}
