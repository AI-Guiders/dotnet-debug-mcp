using DotnetDebugMcp.Services;

namespace DotnetDebugMcp;

internal static class DapHelpers
{
    internal const int DapRetryCount = 3;
    internal const int DapRetryDelayMs = 250;

    /// <summary>Некоторые сборки netcoredbg отклоняют setExceptionBreakpoints (HRESULT 0x80070057) — тогда продолжаем без остановок по исключениям.</summary>
    internal static async Task<bool> TrySetUnhandledExceptionBreakpointsAsync(DapClient client)
    {
        try
        {
            await client.SetExceptionBreakpointsAsync(["unhandled"]).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static (DapClient client, int threadId) GetSessionAndThreadId()
    {
        var client = DebugSession.CurrentClient
            ?? throw new InvalidOperationException("No active debug session. Run debug_launch first.");
        var threadId = DebugSession.LastStoppedThreadId;
        if (threadId == 0)
            throw new InvalidOperationException("Execution has not stopped on a breakpoint yet (no stopped event received). Ensure the target hits a breakpoint after debug_launch, or use debug_continue and wait for the next stop.");
        return (client, threadId);
    }

    internal static bool IsTransientDapError(Exception ex)
    {
        var msg = ex.Message;
        return msg.Contains("running", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("0x80004005", StringComparison.Ordinal)
            || msg.Contains("Failed command", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task WithRetryVoidAsync(Func<Task> action)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (i < DapRetryCount - 1 && IsTransientDapError(ex))
            {
                await Task.Delay(DapRetryDelayMs).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<T> WithRetryAsync<T>(Func<Task<T>> action)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (i < DapRetryCount - 1 && IsTransientDapError(ex))
            {
                await Task.Delay(DapRetryDelayMs).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Путь к исходнику для DAP: совпадает с путями в PDB при сборке из workspace.</summary>
    internal static string ResolveBreakpointFilePath(string workspaceRoot, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return Path.GetFullPath(filePath);
        var trimmed = filePath.Trim();
        if (Path.IsPathRooted(trimmed))
            return Path.GetFullPath(trimmed);
        return Path.GetFullPath(Path.Combine(workspaceRoot, trimmed));
    }

    internal static string FormatException(Exception ex)
    {
        var msg = ex.Message;
        if (ex.InnerException != null)
            msg += "\nInner: " + ex.InnerException.Message;
        msg += "\n" + ex.StackTrace;
        return msg;
    }
}
