using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SwiftAid.Application.Ellai;

/// <summary>One parsed documentation section with provenance.</summary>
public sealed record EllaiDocSection(
    string DocumentId,
    string SectionId,
    string Title,
    string Body,
    string Revision,
    string Status);

/// <summary>One bounded documentation search hit.</summary>
public sealed record EllaiDocHit(
    string SectionId,
    string Title,
    string Snippet,
    string Status,
    string Revision);

/// <summary>File-backed reader for the reviewed technical reference
/// (Task 0). Parses level-2 sections of the approved markdown source;
/// raw repository source files are never indexed or returned. A missing
/// source reports <see cref="IsAvailable"/> false so callers return an
/// explicit unavailable outcome instead of a fabricated answer.</summary>
public sealed class EllaiTechnicalDocs
{
    public const string DocumentId = "technical.md";
    public const int MaxHits = 20;
    public const int MaxSnippetChars = 1200;
    public const int MaxExcerptChars = 6000;

    private static readonly Regex StatusPattern = new(
        @"\*\*Status:\s*([^*]+)\*\*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _path;

    public EllaiTechnicalDocs(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public bool IsAvailable => File.Exists(_path);

    /// <summary>Best-effort path resolution that never throws: an existing
    /// configured path wins, then the container reference mount, then a
    /// content-root walk-up. Falls back to the configured default so a
    /// missing source reports unavailable instead of crashing startup.</summary>
    public static string TryResolvePath(string contentRoot, string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        const string containerPath = "/etc/swiftaid/docs/TECHNICAL.md";
        if (File.Exists(containerPath))
        {
            return containerPath;
        }

        var directory = new DirectoryInfo(contentRoot);
        for (var depth = 0; depth < 4 && directory is not null; depth++)
        {
            var candidate = Path.Combine(directory.FullName, configuredPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        var baseCandidate = Path.Combine(AppContext.BaseDirectory, configuredPath);
        if (File.Exists(baseCandidate))
        {
            return baseCandidate;
        }

        return Path.IsPathRooted(configuredPath) ? configuredPath : containerPath;
    }

    /// <summary>Short content hash of the current source file; binds every
    /// hit and excerpt to the exact reviewed revision.</summary>
    public string Revision()
    {
        var bytes = File.ReadAllBytes(_path);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    /// <summary>Searches section titles then bodies, case-insensitively.
    /// Exact section-id matches rank first. At most 20 hits.</summary>
    public IReadOnlyList<EllaiDocHit> Search(string query, int limit = 10)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<EllaiDocHit>();
        }

        limit = Math.Clamp(limit, 1, MaxHits);
        var sections = LoadSections();
        var trimmed = query.Trim();
        var exact = sections.Where(s =>
            string.Equals(s.SectionId, Slug(trimmed), StringComparison.OrdinalIgnoreCase)).ToList();
        var rest = sections.Except(exact).Where(s =>
            s.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || s.Body.Contains(trimmed, StringComparison.OrdinalIgnoreCase)).ToList();

        return exact.Concat(rest).Take(limit)
            .Select(s => new EllaiDocHit(s.SectionId, s.Title, Snippet(s.Body, trimmed), s.Status, s.Revision))
            .ToList();
    }

    /// <summary>Retrieves one section by exact section id, with a bounded
    /// excerpt. Unknown ids return null; callers deny them explicitly.</summary>
    public EllaiDocSection? Get(string sectionId)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(sectionId))
        {
            return null;
        }

        var section = LoadSections().FirstOrDefault(s =>
            string.Equals(s.SectionId, sectionId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (section is null)
        {
            return null;
        }

        var body = section.Body.Length > MaxExcerptChars
            ? section.Body[..MaxExcerptChars] + "\n…(excerpt truncated at 6,000 chars)"
            : section.Body;
        return section with { Body = body };
    }

    private List<EllaiDocSection> LoadSections()
    {
        var revision = Revision();
        var lines = File.ReadAllLines(_path);
        var sections = new List<EllaiDocSection>();
        string? title = null;
        var body = new StringBuilder();

        void Flush()
        {
            if (title is null)
            {
                return;
            }

            var text = body.ToString().Trim();
            var status = StatusPattern.Match(text) is { Success: true } match
                ? match.Groups[1].Value.Trim()
                : "Unlabeled";
            sections.Add(new EllaiDocSection(DocumentId, Slug(title), title, text, revision, status));
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                title = line[3..].Trim();
                body.Clear();
            }
            else if (title is not null)
            {
                body.AppendLine(line);
            }
        }

        Flush();
        return sections;
    }

    private static string Snippet(string body, string query)
    {
        var index = body.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var start = index < 0 ? 0 : Math.Max(0, index - 300);
        var length = Math.Min(MaxSnippetChars, body.Length - start);
        var snippet = body.Substring(start, length).Trim();
        return (start > 0 ? "…" : string.Empty) + snippet
            + (start + length < body.Length ? "…" : string.Empty);
    }

    internal static string Slug(string title)
    {
        var builder = new StringBuilder(title.Length);
        foreach (var ch in title.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }

        return Regex.Replace(builder.ToString(), "-{2,}", "-").Trim('-');
    }
}
