using System.Text;
using System.Text.Json;
using DotnetDebug.Core;
using DotnetDebugMcp.Services;

namespace DotnetDebugMcp;

internal static class DebugLaunchToolHandlers
{
    internal static async Task<string> HandleDebugLaunch(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (!McpArgumentHelpers.TryGetString(args, "workspace_path", out var workspacePath) || string.IsNullOrWhiteSpace(workspacePath))
            throw new ArgumentException("workspace_path is required.");
        if (!McpArgumentHelpers.TryGetString(args, "target_path", out var targetPath) || string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("target_path is required.");

        var netcoredbgPath = Environment.GetEnvironmentVariable("NETCOREDBG_PATH")?.Trim();
        if (McpArgumentHelpers.TryGetString(args, "netcoredbg_path", out var customPath) && !string.IsNullOrWhiteSpace(customPath))
            netcoredbgPath = customPath;
        if (string.IsNullOrWhiteSpace(netcoredbgPath))
            netcoredbgPath = "netcoredbg";

        var workspaceRoot = Path.GetFullPath(workspacePath!.Trim());
        if (File.Exists(workspaceRoot))
            workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
        var programPath = Path.IsPathRooted(targetPath!.Trim())
            ? Path.GetFullPath(targetPath)
            : Path.GetFullPath(Path.Combine(workspaceRoot, targetPath.Trim()));
        if (!File.Exists(programPath))
            throw new ArgumentException($"Target not found: {programPath}");

        var breakpoints = BreakpointsStorage.GetBreakpoints(workspacePath!, targetPath!).ToList();
        var byFile = breakpoints
            .GroupBy(b => DapHelpers.ResolveBreakpointFilePath(workspaceRoot, b.File))
            .ToDictionary(g => g.Key, g => g.Select(b => (b.Line, b.Condition)).ToList());

        IReadOnlyList<string>? programArgs = null;
        if (args.TryGetValue("program_args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var e in argsEl.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { } s)
                    list.Add(s);
            if (list.Count > 0)
                programArgs = list;
        }

        var client = await DapClient.StartAsync(netcoredbgPath).ConfigureAwait(false);
        client.OnConnectionLost = () =>
        {
            if (DebugSession.CurrentClient == client)
            {
                DebugSession.CurrentClient = null;
                DebugSession.LastStoppedThreadId = 0;
                DebugSession.LastExceptionText = null;
            }
        };
        DebugSession.PrepareStoppedWait();
        var stoppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnEvent = (eventName, body) =>
        {
            if (eventName == "stopped" && body.TryGetProperty("threadId", out var tid))
            {
                var reason = body.TryGetProperty("reason", out var r) ? r.GetString() : null;
                var exceptionText = (reason == "exception" && body.TryGetProperty("text", out var txt)) ? txt.GetString() : null;
                DebugSession.OnStopped(tid.GetInt32(), exceptionText);
                stoppedTcs.TrySetResult();
            }
            else if (eventName == "continued")
                DebugSession.OnContinued();
        };
        var exceptionBpsOk = false;
        try
        {
            await client.LaunchAsync(programPath, Path.GetDirectoryName(programPath), programArgs).ConfigureAwait(false);
            foreach (var (file, list) in byFile)
            {
                if (list.Count > 0)
                    await client.SetBreakpointsAsync(file, list).ConfigureAwait(false);
            }
            exceptionBpsOk = await DapHelpers.TrySetUnhandledExceptionBreakpointsAsync(client).ConfigureAwait(false);
            await client.ConfigurationDoneAsync().ConfigureAwait(false);
            await stoppedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stoppedTcs.TrySetResult();
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        DebugSession.CurrentClient = client;

        var stopped = DebugSession.LastStoppedThreadId != 0;
        if (!stopped)
        {
            try
            {
                var threadsBody = await client.ThreadsAsync().ConfigureAwait(false);
                if (threadsBody != null && threadsBody.Value.TryGetProperty("threads", out var threadsArr))
                {
                    foreach (var t in threadsArr.EnumerateArray())
                    {
                        if (t.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var tid))
                        {
                            DebugSession.LastStoppedThreadId = tid;
                            stopped = true;
                            break;
                        }
                    }
                }
            }
            catch { /* ignore */ }
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Debug session started");
        sb.AppendLine($"# Program: {programPath}");
        sb.AppendLine($"# Breakpoints: {breakpoints.Count} applied");
        sb.AppendLine(exceptionBpsOk
            ? "# Exception breakpoints: unhandled (stop on throw)"
            : "# Exception breakpoints: skipped (adapter rejected setExceptionBreakpoints; some netcoredbg builds return 0x80070057)");
        if (!stopped)
            sb.AppendLine("# (Wait for breakpoint timed out — call debug_continue then use stack_trace/step_* after it stops, or check that the target hits a breakpoint. First thread id used as fallback if available.)");
        else if (DebugSession.LastExceptionText is { } exMsg)
            sb.AppendLine($"# Stopped on exception: {exMsg}");
        sb.AppendLine("# Use debug_continue or debug_step_over to control execution.");
        return sb.ToString();
    }

    internal static async Task<string> HandleDebugAttach(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (!McpArgumentHelpers.TryGetString(args, "workspace_path", out var workspacePath) || string.IsNullOrWhiteSpace(workspacePath))
            throw new ArgumentException("workspace_path is required.");
        if (!args.TryGetValue("process_id", out var pidEl) || !pidEl.TryGetInt32(out var processId) || processId <= 0)
            throw new ArgumentException("process_id (positive integer) is required.");

        var netcoredbgPath = Environment.GetEnvironmentVariable("NETCOREDBG_PATH")?.Trim();
        if (McpArgumentHelpers.TryGetString(args, "netcoredbg_path", out var customPath) && !string.IsNullOrWhiteSpace(customPath))
            netcoredbgPath = customPath;
        if (string.IsNullOrWhiteSpace(netcoredbgPath))
            netcoredbgPath = "netcoredbg";

        var workspaceRoot = Path.GetFullPath(workspacePath!.Trim());
        if (File.Exists(workspaceRoot))
            workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;

        var breakpoints = new List<BreakpointsStorage.BreakpointEntry>();
        var byFile = new Dictionary<string, List<(int Line, string? Condition)>>();
        if (McpArgumentHelpers.TryGetString(args, "target_path", out var targetPath) && !string.IsNullOrWhiteSpace(targetPath))
        {
            breakpoints = BreakpointsStorage.GetBreakpoints(workspacePath, targetPath).ToList();
            byFile = breakpoints
                .GroupBy(b => DapHelpers.ResolveBreakpointFilePath(workspaceRoot, b.File))
                .ToDictionary(g => g.Key, g => g.Select(b => (b.Line, b.Condition)).ToList());
        }

        var client = await DapClient.StartAsync(netcoredbgPath).ConfigureAwait(false);
        client.OnConnectionLost = () =>
        {
            if (DebugSession.CurrentClient == client)
            {
                DebugSession.CurrentClient = null;
                DebugSession.LastStoppedThreadId = 0;
                DebugSession.LastExceptionText = null;
            }
        };
        DebugSession.PrepareStoppedWait();
        var stoppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnEvent = (eventName, body) =>
        {
            if (eventName == "stopped" && body.TryGetProperty("threadId", out var tid))
            {
                var reason = body.TryGetProperty("reason", out var r) ? r.GetString() : null;
                var exceptionText = (reason == "exception" && body.TryGetProperty("text", out var txt)) ? txt.GetString() : null;
                DebugSession.OnStopped(tid.GetInt32(), exceptionText);
                stoppedTcs.TrySetResult();
            }
            else if (eventName == "continued")
                DebugSession.OnContinued();
        };
        var attachExceptionBpsOk = false;
        try
        {
            await client.AttachAsync(processId).ConfigureAwait(false);
            foreach (var (file, list) in byFile)
            {
                if (list.Count > 0)
                    await client.SetBreakpointsAsync(file, list).ConfigureAwait(false);
            }
            attachExceptionBpsOk = await DapHelpers.TrySetUnhandledExceptionBreakpointsAsync(client).ConfigureAwait(false);
            await client.ConfigurationDoneAsync().ConfigureAwait(false);
            await stoppedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stoppedTcs.TrySetResult();
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        DebugSession.CurrentClient = client;

        var stopped = DebugSession.LastStoppedThreadId != 0;
        if (!stopped)
        {
            try
            {
                var threadsBody = await client.ThreadsAsync().ConfigureAwait(false);
                if (threadsBody != null && threadsBody.Value.TryGetProperty("threads", out var threadsArr))
                {
                    foreach (var t in threadsArr.EnumerateArray())
                    {
                        if (t.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var tid))
                        {
                            DebugSession.LastStoppedThreadId = tid;
                            stopped = true;
                            break;
                        }
                    }
                }
            }
            catch { /* ignore */ }
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Debug session started (attach)");
        sb.AppendLine($"# Process ID: {processId}");
        sb.AppendLine($"# Breakpoints: {breakpoints.Count} applied");
        sb.AppendLine(attachExceptionBpsOk
            ? "# Exception breakpoints: unhandled (stop on throw)"
            : "# Exception breakpoints: skipped (adapter rejected setExceptionBreakpoints; some netcoredbg builds return 0x80070057)");
        if (!stopped)
            sb.AppendLine("# (Wait for breakpoint timed out — call debug_continue then use stack_trace/step_* after it stops.)");
        else if (DebugSession.LastExceptionText is { } exMsg)
            sb.AppendLine($"# Stopped on exception: {exMsg}");
        sb.AppendLine("# Use debug_continue or debug_step_over to control execution.");
        return sb.ToString();
    }
}
