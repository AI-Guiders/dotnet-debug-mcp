using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDebugMcp.Services;

/// <summary>Хранение брейкпоинтов в JSON-файле в каталоге workspace. Ключ — target (путь к .csproj или exe), значение — список брейкпоинтов.</summary>
public static class BreakpointsStorage
{
    public const string FileName = ".dotnet-debug-mcp-breakpoints.json";

    public record BreakpointEntry(string File, int Line, string? Condition = null);

    public record StorageModel(Dictionary<string, List<BreakpointEntry>> Targets)
    {
        public static StorageModel Empty() => new(new Dictionary<string, List<BreakpointEntry>>());
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string GetFilePath(string workspacePath)
    {
        var dir = Path.GetFullPath(workspacePath.Trim());
        if (File.Exists(dir))
            dir = Path.GetDirectoryName(dir) ?? dir;
        return Path.Combine(dir, FileName);
    }

    /// <summary>Единый ключ для target: при относительном пути — относительно workspace, чтобы set (relative) и launch (absolute) находили одни и те же брейкпоинты.</summary>
    private static string NormalizeTarget(string workspacePath, string targetPath)
    {
        var t = targetPath.Trim();
        if (string.IsNullOrEmpty(t))
            return t;
        if (Path.IsPathRooted(t))
            return Path.GetFullPath(t);
        var ws = Path.GetFullPath(workspacePath.Trim());
        if (File.Exists(ws))
            ws = Path.GetDirectoryName(ws) ?? ws;
        return Path.GetFullPath(Path.Combine(ws, t));
    }

    public static StorageModel Load(string workspacePath)
    {
        var path = GetFilePath(workspacePath);
        if (!File.Exists(path))
            return StorageModel.Empty();
        try
        {
            var json = File.ReadAllText(path);
            var model = JsonSerializer.Deserialize<StorageModel>(json, JsonOptions);
            if (model?.Targets != null)
                return model;
        }
        catch { /* ignore */ }
        return StorageModel.Empty();
    }

    public static void Save(string workspacePath, StorageModel model)
    {
        var path = GetFilePath(workspacePath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(model, JsonOptions));
    }

    public static void SetBreakpoints(string workspacePath, string targetPath, IReadOnlyList<BreakpointEntry> breakpoints)
    {
        var model = Load(workspacePath);
        var key = NormalizeTarget(workspacePath, targetPath);
        model.Targets[key] = breakpoints.ToList();
        Save(workspacePath, model);
    }

    public static IReadOnlyList<BreakpointEntry> GetBreakpoints(string workspacePath, string? targetPath)
    {
        var model = Load(workspacePath);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return model.Targets.Values.SelectMany(x => x).ToList();
        }
        var key = NormalizeTarget(workspacePath, targetPath);
        return model.Targets.TryGetValue(key, out var list) ? list : Array.Empty<BreakpointEntry>();
    }

    public static void ClearBreakpoints(string workspacePath, string? targetPath)
    {
        var model = Load(workspacePath);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            model.Targets.Clear();
        }
        else
        {
            var key = NormalizeTarget(workspacePath, targetPath);
            model.Targets.Remove(key);
        }
        Save(workspacePath, model);
    }

    public static IReadOnlyDictionary<string, List<BreakpointEntry>> ListTargets(string workspacePath)
    {
        return Load(workspacePath).Targets;
    }
}
