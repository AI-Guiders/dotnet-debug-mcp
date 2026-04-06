using System.Text;
using System.Text.Json;
using DotnetDebug.Core;

namespace DotnetDebugMcp;

internal static class DebugControlToolHandlers
{
    internal static async Task<string> HandleDebugContinue(IReadOnlyDictionary<string, JsonElement> _)
    {
        var (client, threadId) = DapHelpers.GetSessionAndThreadId();
        await client.ContinueAsync(threadId).ConfigureAwait(false);
        return "# Continued execution.";
    }

    internal static async Task<string> HandleDebugStepOver(IReadOnlyDictionary<string, JsonElement> _)
    {
        try { await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { return "# Timeout (5s) waiting for execution to stop."; }
        var (client, threadId) = DapHelpers.GetSessionAndThreadId();
        try { await DapHelpers.WithRetryVoidAsync(() => client.NextAsync(threadId)).ConfigureAwait(false); }
        catch (InvalidOperationException ex) { return "# " + ex.Message; }
        return "# Step over sent; execution will stop at next line.";
    }

    internal static async Task<string> HandleDebugStepInto(IReadOnlyDictionary<string, JsonElement> _)
    {
        try { await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { return "# Timeout (5s) waiting for execution to stop."; }
        var (client, threadId) = DapHelpers.GetSessionAndThreadId();
        try { await DapHelpers.WithRetryVoidAsync(() => client.StepInAsync(threadId)).ConfigureAwait(false); }
        catch (InvalidOperationException ex) { return "# " + ex.Message; }
        return "# Step into sent; execution will stop inside the call.";
    }

    internal static async Task<string> HandleDebugStepOut(IReadOnlyDictionary<string, JsonElement> _)
    {
        try { await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { return "# Timeout (5s) waiting for execution to stop."; }
        var (client, threadId) = DapHelpers.GetSessionAndThreadId();
        try { await DapHelpers.WithRetryVoidAsync(() => client.StepOutAsync(threadId)).ConfigureAwait(false); }
        catch (InvalidOperationException ex) { return "# " + ex.Message; }
        return "# Step out sent; execution will stop at caller.";
    }

    internal static async Task<string> HandleDebugStop(IReadOnlyDictionary<string, JsonElement> _)
    {
        var client = DebugSession.CurrentClient;
        if (client == null)
            return "# No active debug session; nothing to stop.";
        var threadId = DebugSession.LastStoppedThreadId;
        DebugSession.CurrentClient = null;
        DebugSession.LastStoppedThreadId = 0;
        if (threadId != 0)
            try { await client.ContinueAsync(threadId).ConfigureAwait(false); } catch { /* целевой процесс продолжит выполнение перед отключением */ }
        await client.DisposeAsync().ConfigureAwait(false);
        return "# Debug session stopped; target resumed, client disposed.";
    }

    internal static async Task<string> HandleDebugStackTrace(IReadOnlyDictionary<string, JsonElement> _)
    {
        try
        {
            await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "# Timeout (5s) waiting for execution to stop. Run debug_continue and try again after the next break.";
        }
        var (client, threadId) = DapHelpers.GetSessionAndThreadId();
        JsonElement? body;
        try
        {
            body = await DapHelpers.WithRetryAsync(() => client.StackTraceAsync(threadId)).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return "# " + ex.Message;
        }
        if (body == null || !body.Value.TryGetProperty("stackFrames", out var frames))
            return "# No stack frames.";
        var sb = new StringBuilder();
        sb.AppendLine("# Stack trace");
        var i = 0;
        foreach (var f in frames.EnumerateArray())
        {
            var name = f.TryGetProperty("name", out var n) ? n.GetString() : "?";
            var line = f.TryGetProperty("line", out var ln) ? ln.GetInt32() : 0;
            var path = "";
            if (f.TryGetProperty("source", out var src) && src.TryGetProperty("path", out var p))
                path = p.GetString() ?? "";
            var id = f.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
            sb.AppendLine($"  [{i}] {name} — {path}:{line} (id={id})");
            i++;
        }
        return sb.ToString();
    }

    internal static async Task<string> HandleDebugVariables(IReadOnlyDictionary<string, JsonElement> args)
    {
        try
        {
            await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "# Timeout (5s) waiting for execution to stop. Run debug_continue and try again after the next break.";
        }
        var (client, threadId) = DapHelpers.GetSessionAndThreadId();
        var frameIndex = 0;
        if (args.TryGetValue("frame_index", out var fiEl) && fiEl.ValueKind == JsonValueKind.Number && fiEl.TryGetInt32(out var fi))
            frameIndex = fi;
        JsonElement? stackBody;
        try
        {
            stackBody = await DapHelpers.WithRetryAsync(() => client.StackTraceAsync(threadId)).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return "# " + ex.Message;
        }
        if (stackBody == null || !stackBody.Value.TryGetProperty("stackFrames", out var frames))
            return "# No stack; run debug_stack_trace first or ensure stopped.";
        var frameList = frames.EnumerateArray().ToList();
        if (frameIndex < 0 || frameIndex >= frameList.Count)
            return $"# frame_index {frameIndex} out of range (0..{frameList.Count - 1}).";
        var frame = frameList[frameIndex];
        if (!frame.TryGetProperty("id", out var idEl))
            return "# Frame has no id.";
        var frameId = idEl.GetInt32();
        var sb = new StringBuilder();
        sb.AppendLine($"# Variables (frame {frameIndex})");

        // Сначала пробуем scopes: netcoredbg и др. отдают переменные только через scope.variablesReference.
        var usedScopes = false;
        try
        {
            var scopesBody = await DapHelpers.WithRetryAsync(() => client.ScopesAsync(frameId)).ConfigureAwait(false);
            if (scopesBody != null && scopesBody.Value.TryGetProperty("scopes", out var scopesArr))
            {
                foreach (var scope in scopesArr.EnumerateArray())
                {
                    if (!scope.TryGetProperty("variablesReference", out var vrefEl) || !vrefEl.TryGetInt32(out var vref) || vref == 0)
                        continue;
                    var scopeName = scope.TryGetProperty("name", out var sn) ? sn.GetString() : "?";
                    var varsBody = await DapHelpers.WithRetryAsync(() => client.VariablesAsync(vref)).ConfigureAwait(false);
                    if (varsBody == null || !varsBody.Value.TryGetProperty("variables", out var vars))
                        continue;
                    usedScopes = true;
                    sb.AppendLine($"## {scopeName}");
                    await DapVariableExpansion.AppendExpandedVariablesAsync(
                        client,
                        sb,
                        vars,
                        indent: "  ",
                        depth: 0,
                        maxDepth: DapVariableExpansion.DefaultMaxDepth,
                        maxChildrenPerNode: DapVariableExpansion.DefaultMaxChildrenPerNode,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // scopes не поддерживается или ошибка — пробуем variables(frameId) напрямую
        }

        if (!usedScopes)
        {
            JsonElement? varsBody;
            try
            {
                varsBody = await DapHelpers.WithRetryAsync(() => client.VariablesAsync(frameId)).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return "# " + ex.Message;
            }
            if (varsBody == null || !varsBody.Value.TryGetProperty("variables", out var vars))
                return "# No variables for this frame (tried scopes and direct variables).";
            await DapVariableExpansion.AppendExpandedVariablesAsync(
                client,
                sb,
                vars,
                indent: "  ",
                depth: 0,
                maxDepth: DapVariableExpansion.DefaultMaxDepth,
                maxChildrenPerNode: DapVariableExpansion.DefaultMaxChildrenPerNode,
                CancellationToken.None).ConfigureAwait(false);
        }

        return sb.ToString();
    }
}
