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
        DebugSession.Clear();
        if (threadId != 0)
            try { await client.ContinueAsync(threadId).ConfigureAwait(false); } catch { /* целевой процесс продолжит работу перед отключением */ }
        await client.DisposeAsync().ConfigureAwait(false);
        return "# Debug session stopped; target resumed, client disposed.";
    }

    /// <summary>
    /// Один ответ после stopped — делегирует в <see cref="DapStopContext"/> (Core; shared with CIDE in-proc).
    /// </summary>
    internal static async Task<string> HandleDebugStopContext(IReadOnlyDictionary<string, JsonElement> args)
    {
        try
        {
            await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "# Timeout (5s) waiting for execution to stop. Run debug_continue and try again after the next break.";
        }

        var (client, _) = DapHelpers.GetSessionAndThreadId();
        var meta = new DapStopContextMeta(
            DebugSession.LastStoppedThreadId,
            DebugSession.WorkspacePath,
            DebugSession.TargetPath,
            DebugSession.LastExceptionText);
        return await DapStopContext.FormatMarkdownAsync(client, meta, ParseFrameOptions(args)).ConfigureAwait(false);
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
        return await DapFrameInspection.FormatStackTraceMarkdownAsync(client, threadId).ConfigureAwait(false);
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
        return await DapFrameInspection.FormatVariablesAsync(client, threadId, ParseFrameOptions(args)).ConfigureAwait(false);
    }

    static DapFrameInspectionOptions ParseFrameOptions(IReadOnlyDictionary<string, JsonElement> args)
    {
        var frameIndex = 0;
        if (args.TryGetValue("frame_index", out var fiEl) && fiEl.ValueKind == JsonValueKind.Number && fiEl.TryGetInt32(out var fi))
            frameIndex = fi;
        var fast = args.TryGetValue("fast", out var fastEl) && fastEl.ValueKind == JsonValueKind.True;
        var maxDepthDefault = fast ? 0 : DapVariableExpansion.DefaultMaxDepth;
        var maxChildrenDefault = fast ? 24 : DapVariableExpansion.DefaultMaxChildrenPerNode;
        var maxDepth = McpArgumentHelpers.GetOptionalClampedInt32(args, "max_depth", maxDepthDefault, min: 0, max: 32);
        var maxChildren = McpArgumentHelpers.GetOptionalClampedInt32(args, "max_children_per_node", maxChildrenDefault, min: 1, max: 256);
        var timeBudgetMs = McpArgumentHelpers.GetOptionalClampedInt32(args, "time_budget_ms", fast ? 700 : 1800, min: 100, max: 10000);
        var formatJson = args.TryGetValue("format", out var fmtEl) &&
            fmtEl.ValueKind == JsonValueKind.String &&
            string.Equals(fmtEl.GetString(), "json", StringComparison.OrdinalIgnoreCase);
        var jsonIndented = !args.TryGetValue("json_indented", out var jindEl) || jindEl.ValueKind != JsonValueKind.False;
        return new DapFrameInspectionOptions
        {
            FrameIndex = frameIndex,
            Fast = fast,
            MaxDepth = maxDepth,
            MaxChildrenPerNode = maxChildren,
            TimeBudgetMs = timeBudgetMs,
            FormatJson = formatJson,
            JsonIndented = jsonIndented,
        };
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