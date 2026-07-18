using System.Text;
using DotnetDebug.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDebugMcp;

/// <summary>
/// Read-only MCP resources for live session snapshots (inspired by peer MIT debug://*; not a code port).
/// </summary>
internal static class DebugResourceHandlers
{
    internal const string StateUri = "debug://state";
    internal const string BreakpointsUri = "debug://breakpoints";
    internal const string ThreadsUri = "debug://threads";

    private static readonly List<Resource> Catalog =
    [
        new()
        {
            Uri = StateUri,
            Name = "Debug session state",
            Description = "Active session flag, threadId, exception, workspace/target.",
            MimeType = "text/plain",
        },
        new()
        {
            Uri = BreakpointsUri,
            Name = "Saved breakpoints",
            Description = "Breakpoints for current workspace+target from storage JSON.",
            MimeType = "text/plain",
        },
        new()
        {
            Uri = ThreadsUri,
            Name = "DAP threads",
            Description = "Thread list from the active DAP session, if any.",
            MimeType = "text/plain",
        },
    ];

    internal static ValueTask<ListResourcesResult> ListAsync(
        RequestContext<ListResourcesRequestParams> _,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ListResourcesResult { Resources = Catalog });
    }

    internal static async ValueTask<ReadResourceResult> ReadAsync(
        RequestContext<ReadResourceRequestParams> request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.Params?.Uri?.Trim() ?? "";
        var text = uri switch
        {
            StateUri => FormatState(),
            BreakpointsUri => FormatBreakpoints(),
            ThreadsUri => await FormatThreadsAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown resource URI: {uri}. Known: {StateUri}, {BreakpointsUri}, {ThreadsUri}."),
        };
        return new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = uri,
                    MimeType = "text/plain",
                    Text = text,
                },
            ],
        };
    }

    private static string FormatState()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# debug://state");
        var active = DebugSession.CurrentClient != null;
        sb.AppendLine($"active={active.ToString().ToLowerInvariant()}");
        sb.AppendLine($"threadId={DebugSession.LastStoppedThreadId}");
        sb.AppendLine($"stopped={(DebugSession.LastStoppedThreadId != 0).ToString().ToLowerInvariant()}");
        if (DebugSession.WorkspacePath is { } ws)
            sb.AppendLine($"workspace={ws}");
        if (DebugSession.TargetPath is { } tp)
            sb.AppendLine($"target={tp}");
        if (DebugSession.LastExceptionText is { } ex)
            sb.AppendLine($"exception={ex}");
        return sb.ToString();
    }

    private static string FormatBreakpoints()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# debug://breakpoints");
        var ws = DebugSession.WorkspacePath;
        if (string.IsNullOrWhiteSpace(ws))
        {
            sb.AppendLine("# No workspace on session (launch/attach first).");
            return sb.ToString();
        }

        sb.AppendLine($"workspace={ws}");
        var target = DebugSession.TargetPath;
        if (!string.IsNullOrWhiteSpace(target))
        {
            sb.AppendLine($"target={target}");
            var list = BreakpointsStorage.GetBreakpoints(ws, target);
            sb.AppendLine($"count={list.Count}");
            foreach (var b in list)
            {
                var cond = string.IsNullOrEmpty(b.Condition) ? "" : $" condition={b.Condition}";
                sb.AppendLine($"  {b.File}:{b.Line}{cond}");
            }
            return sb.ToString();
        }

        var targets = BreakpointsStorage.ListTargets(ws);
        sb.AppendLine($"targets={targets.Count}");
        foreach (var (key, bps) in targets)
        {
            sb.AppendLine($"## {key} ({bps.Count})");
            foreach (var b in bps)
            {
                var cond = string.IsNullOrEmpty(b.Condition) ? "" : $" condition={b.Condition}";
                sb.AppendLine($"  {b.File}:{b.Line}{cond}");
            }
        }
        return sb.ToString();
    }

    private static async Task<string> FormatThreadsAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# debug://threads");
        var client = DebugSession.CurrentClient;
        if (client == null)
        {
            sb.AppendLine("# No active debug session.");
            return sb.ToString();
        }

        try
        {
            var body = await client.ThreadsAsync(cancellationToken).ConfigureAwait(false);
            if (body == null || !body.Value.TryGetProperty("threads", out var threads))
            {
                sb.AppendLine("# No threads in DAP response.");
                return sb.ToString();
            }

            foreach (var t in threads.EnumerateArray())
            {
                var id = t.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var tid) ? tid : 0;
                var name = t.TryGetProperty("name", out var n) ? n.GetString() : "?";
                sb.AppendLine($"  [{id}] {name}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"# Error: {DapHelpers.FormatException(ex)}");
        }

        return sb.ToString();
    }
}
