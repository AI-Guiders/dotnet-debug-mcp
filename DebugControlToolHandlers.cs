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
        var fast = args.TryGetValue("fast", out var fastEl) && fastEl.ValueKind == JsonValueKind.True;
        var maxDepthDefault = fast ? 0 : DapVariableExpansion.DefaultMaxDepth;
        var maxChildrenDefault = fast ? 24 : DapVariableExpansion.DefaultMaxChildrenPerNode;
        var maxDepth = McpArgumentHelpers.GetOptionalClampedInt32(
            args,
            "max_depth",
            maxDepthDefault,
            min: 0,
            max: 32);
        var maxChildren = McpArgumentHelpers.GetOptionalClampedInt32(
            args,
            "max_children_per_node",
            maxChildrenDefault,
            min: 1,
            max: 256);
        var timeBudgetMs = McpArgumentHelpers.GetOptionalClampedInt32(
            args,
            "time_budget_ms",
            fast ? 700 : 1800,
            min: 100,
            max: 10000);
        var formatJson = args.TryGetValue("format", out var fmtEl) &&
            fmtEl.ValueKind == JsonValueKind.String &&
            string.Equals(fmtEl.GetString(), "json", StringComparison.OrdinalIgnoreCase);
        var jsonIndented = !args.TryGetValue("json_indented", out var jindEl) || jindEl.ValueKind != JsonValueKind.False;
        using var budgetCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeBudgetMs));
        var ct = budgetCts.Token;

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
        var scopeBlocks = new List<(string Name, JsonElement Variables)>();
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
                    scopeBlocks.Add((scopeName ?? "?", vars));
                }
            }
        }
        catch (InvalidOperationException)
        {
            // scopes не поддерживается — ниже direct variables
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
            scopeBlocks.Add(("variables", vars));
        }

        if (scopeBlocks.Count == 0)
            return "# No variable scopes for this frame.";

        var partial = false;
        string? partialNote = null;

        if (formatJson)
        {
            var built = new List<(string ScopeName, IReadOnlyList<DapVariableTreeNode> Roots)>(scopeBlocks.Count);
            foreach (var (name, varEl) in scopeBlocks)
            {
                try
                {
                    var tree = await DapVariableExpansion
                        .BuildExpandedTreeAsync(client, varEl, maxDepth, maxChildren, ct)
                        .ConfigureAwait(false);
                    built.Add((name, tree));
                }
                catch (OperationCanceledException)
                {
                    partial = true;
                    partialNote = $"Stopped by time budget ({timeBudgetMs} ms). Use fast=true, lower max_depth/max_children_per_node, or inspect via debug_variable_children.";
                    break;
                }
            }

            return DapVariableExpansion.SerializeFrameVariablesDocumentToJson(
                frameIndex,
                maxDepth,
                maxChildren,
                built,
                partial,
                partialNote,
                jsonIndented);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Variables (frame {frameIndex})");
        sb.AppendLine($"# (max_depth={maxDepth}, max_children_per_node={maxChildren}, time_budget_ms={timeBudgetMs}, fast={fast.ToString().ToLowerInvariant()})");
        foreach (var (name, varEl) in scopeBlocks)
        {
            sb.AppendLine($"## {name}");
            try
            {
                await DapVariableExpansion
                    .AppendExpandedVariablesAsync(
                        client,
                        sb,
                        varEl,
                        indent: "  ",
                        depth: 0,
                        maxDepth,
                        maxChildren,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                sb.AppendLine($"# Partial: stopped by time budget ({timeBudgetMs} ms).");
                sb.AppendLine("# Tip: use fast=true, lower max_depth/max_children_per_node, and expand specific refs via debug_variable_children.");
                break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Один уровень детей по <c>variablesReference</c> (без рекурсии); тяжёлый кадр — сначала <c>debug_variables</c> с format=json и малым max_depth, потом сюда.</summary>
    internal static async Task<string> HandleDebugVariableChildren(IReadOnlyDictionary<string, JsonElement> args)
    {
        try
        {
            await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "# Timeout (5s) waiting for execution to stop. Run debug_continue and try again after the next break.";
        }
        if (!args.TryGetValue("variables_reference", out var vrefTop) || vrefTop.ValueKind != JsonValueKind.Number || !vrefTop.TryGetInt32(out var vref) || vref == 0)
            return "# variables_reference is required and must be a non-zero integer (from debug_variables JSON or DAP).";

        int? parentIndexed = null;
        if (args.TryGetValue("indexed_variables", out var ivEl) && ivEl.ValueKind == JsonValueKind.Number && ivEl.TryGetInt32(out var iv) && iv >= 0)
            parentIndexed = iv;
        int? parentNamed = null;
        if (args.TryGetValue("named_variables", out var nvEl) && nvEl.ValueKind == JsonValueKind.Number && nvEl.TryGetInt32(out var nv) && nv >= 0)
            parentNamed = nv;
        var maxChildren = McpArgumentHelpers.GetOptionalClampedInt32(
            args,
            "max_children",
            DapVariableExpansion.DefaultMaxChildrenPerNode,
            min: 1,
            max: 256);
        var jsonIndented = !args.TryGetValue("json_indented", out var jindEl) || jindEl.ValueKind != JsonValueKind.False;

        var (client, _) = DapHelpers.GetSessionAndThreadId();
        var body = await DapVariableExpansion
            .FetchChildVariablesBodyAsync(
                client,
                vref,
                parentNamed,
                parentIndexed,
                maxChildren,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (body == null || !body.Value.TryGetProperty("variables", out var vars))
            return "# No children for this variables_reference (or DAP error).";
        if (vars.GetArrayLength() == 0)
            return DapVariableExpansion.SerializeVariableListToJson(Array.Empty<DapVariableTreeNode>(), jsonIndented);

        var oneLevel = await DapVariableExpansion
            .BuildExpandedTreeAsync(client, vars, maxDepth: 0, maxChildren, CancellationToken.None)
            .ConfigureAwait(false);
        return DapVariableExpansion.SerializeVariableListToJson(oneLevel, jsonIndented);
    }
}
