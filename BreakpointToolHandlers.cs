using System.Text;
using System.Text.Json;
using DotnetDebug.Core;

namespace DotnetDebugMcp;

internal static class BreakpointToolHandlers
{
    internal static string HandleSetBreakpoints(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (!McpArgumentHelpers.TryGetString(args, "workspace_path", out var workspacePath) || string.IsNullOrWhiteSpace(workspacePath))
            throw new ArgumentException("workspace_path is required.");
        if (!McpArgumentHelpers.TryGetString(args, "target_path", out var targetPath) || string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("target_path is required.");
        if (!args.TryGetValue("breakpoints", out var bpEl) || bpEl.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("breakpoints (array) is required.");

        var list = new List<BreakpointsStorage.BreakpointEntry>();
        foreach (var item in bpEl.EnumerateArray())
        {
            if (!McpArgumentHelpers.TryGetPropString(item, "file_path", out var file) || !McpArgumentHelpers.TryGetPropInt(item, "line", out var line) || line < 1)
                continue;
            McpArgumentHelpers.TryGetPropString(item, "condition", out var condition);
            list.Add(new BreakpointsStorage.BreakpointEntry(file!, line, string.IsNullOrWhiteSpace(condition) ? null : condition));
        }

        BreakpointsStorage.SetBreakpoints(workspacePath!, targetPath!, list);
        var sb = new StringBuilder();
        sb.AppendLine("# Breakpoints set");
        sb.AppendLine($"# Target: {targetPath}");
        sb.AppendLine($"# File: {BreakpointsStorage.FileName} in workspace");
        sb.AppendLine($"# Count: {list.Count}");
        foreach (var bp in list)
            sb.AppendLine($"  {bp.File}:{bp.Line}" + (bp.Condition != null ? $" condition=\"{bp.Condition}\"" : ""));
        return sb.ToString();
    }

    internal static string HandleListBreakpoints(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (!McpArgumentHelpers.TryGetString(args, "workspace_path", out var workspacePath) || string.IsNullOrWhiteSpace(workspacePath))
            throw new ArgumentException("workspace_path is required.");
        McpArgumentHelpers.TryGetString(args, "target_path", out var targetPath);

        var sb = new StringBuilder();
        sb.AppendLine("# Saved breakpoints");
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            var bps = BreakpointsStorage.GetBreakpoints(workspacePath!, targetPath);
            sb.AppendLine($"# Target: {targetPath}");
            sb.AppendLine($"# Count: {bps.Count}");
            foreach (var bp in bps)
                sb.AppendLine($"  {bp.File}:{bp.Line}" + (bp.Condition != null ? $" condition=\"{bp.Condition}\"" : ""));
        }
        else
        {
            var targets = BreakpointsStorage.ListTargets(workspacePath!);
            foreach (var (target, bps) in targets)
            {
                sb.AppendLine($"# Target: {target}");
                foreach (var bp in bps)
                    sb.AppendLine($"  {bp.File}:{bp.Line}" + (bp.Condition != null ? $" condition=\"{bp.Condition}\"" : ""));
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    internal static string HandleClearBreakpoints(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (!McpArgumentHelpers.TryGetString(args, "workspace_path", out var workspacePath) || string.IsNullOrWhiteSpace(workspacePath))
            throw new ArgumentException("workspace_path is required.");
        McpArgumentHelpers.TryGetString(args, "target_path", out var targetPath);
        BreakpointsStorage.ClearBreakpoints(workspacePath!, targetPath);
        return string.IsNullOrWhiteSpace(targetPath)
            ? "# All breakpoints cleared for this workspace."
            : $"# Breakpoints cleared for target: {targetPath}";
    }
}
