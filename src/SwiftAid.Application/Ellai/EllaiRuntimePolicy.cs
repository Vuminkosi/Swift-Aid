namespace SwiftAid.Application.Ellai;

/// <summary>Pinned runtime LLM policy (ELL policy pipeline). The approved
/// policy file is loaded once at startup; every guarded reasoning call
/// carries this exact text as its system message, stamped with version
/// and hash. Any load failure, engineering-content leak, or hash mismatch
/// fails closed — no model call runs without the pinned policy.</summary>
public sealed record EllaiRuntimePolicy(string Version, string Text, string Hash)
{
    public static EllaiRuntimePolicy Load(string path, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Ellai runtime policy file is missing: {path}.");
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Ellai runtime policy file is empty; refusing to run ungoverned.");
        }

        if (!EllaiPromptLoader.ExcludesEngineeringContent(text))
        {
            throw new InvalidOperationException("Ellai runtime policy contains engineering content; refusing to load.");
        }

        return new EllaiRuntimePolicy(version, text, EllaiPromptLoader.ComputeHash(text));
    }

    /// <summary>Resolves the configured policy path: absolute as-is, else
    /// relative to the content root (walking up for local source layouts),
    /// else relative to the app base. The file is referenced, never
    /// embedded: Docker bind-mounts it read-only.</summary>
    public static string ResolvePath(string contentRoot, string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        if (Path.IsPathRooted(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var candidates = new List<string>
        {
            Path.Combine(contentRoot, configuredPath),
            Path.Combine(AppContext.BaseDirectory, configuredPath),
        };
        var directory = new DirectoryInfo(contentRoot);
        for (var depth = 0; depth < 4 && directory is not null; depth++)
        {
            candidates.Add(Path.Combine(directory.FullName, configuredPath));
            directory = directory.Parent;
        }

        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Ellai runtime policy file not found: {configuredPath} (content root: {contentRoot}).");
    }

    /// <summary>Throws when the presented hash is not the pinned one, so a
    /// session started under a rotated policy cannot silently continue.</summary>
    public void RequireCurrent(string hash)
    {
        if (!string.Equals(hash, Hash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Ellai runtime policy rotated mid-session; reauthorization required.");
        }
    }
}
