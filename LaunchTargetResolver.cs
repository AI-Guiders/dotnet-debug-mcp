namespace DotnetDebugMcp;

/// <summary>
/// DAP launch needs a .dll/.exe; breakpoint storage keys on .csproj (or explicit binary).
/// </summary>
internal static class LaunchTargetResolver
{
    public static bool IsProjectFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBinary(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve program path for DAP Launch. Keeps .dll/.exe; maps project → newest matching dll under bin/.
    /// </summary>
    public static string ResolveBinary(string targetPath)
    {
        var full = Path.GetFullPath(targetPath);
        if (!File.Exists(full))
            throw new ArgumentException($"Target not found: {full}");

        if (IsBinary(full))
            return full;

        if (IsProjectFile(full))
        {
            var name = Path.GetFileNameWithoutExtension(full);
            var projectDir = Path.GetDirectoryName(full)
                ?? throw new ArgumentException($"No directory for project: {full}");
            var binDir = Path.Combine(projectDir, "bin");
            if (!Directory.Exists(binDir))
                throw new ArgumentException(
                    $"No bin/ under {projectDir} — build first (cdp_build), then launch.");

            var dllName = name + ".dll";
            var candidates = Directory.EnumerateFiles(binDir, dllName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            if (candidates.Count == 0)
                throw new ArgumentException(
                    $"No built {dllName} under {binDir} — build first (cdp_build).");

            return candidates[0];
        }

        throw new ArgumentException(
            $"target_path must be .dll/.exe or project (.csproj); got: {full}");
    }

    public static string? TryResolveBinary(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return null;
        try
        {
            return ResolveBinary(targetPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Working directory for launch: project dir when keyed by csproj; else binary directory.</summary>
    public static string ResolveWorkingDirectory(string targetPath, string binaryPath)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        if (IsProjectFile(fullTarget))
            return Path.GetDirectoryName(fullTarget) ?? Path.GetDirectoryName(binaryPath) ?? ".";
        return Path.GetDirectoryName(Path.GetFullPath(binaryPath)) ?? ".";
    }
}
