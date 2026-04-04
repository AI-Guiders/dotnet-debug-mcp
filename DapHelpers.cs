using DotnetDebug.Core;

namespace DotnetDebugMcp;

internal static class DapHelpers
{
    internal static Task<bool> TrySetUnhandledExceptionBreakpointsAsync(DapClient client) =>
        DapShared.TrySetUnhandledExceptionBreakpointsAsync(client);

    internal static (DapClient client, int threadId) GetSessionAndThreadId()
    {
        var client = DebugSession.CurrentClient
            ?? throw new InvalidOperationException("No active debug session. Run debug_launch first.");
        var threadId = DebugSession.LastStoppedThreadId;
        if (threadId == 0)
            throw new InvalidOperationException("Execution has not stopped on a breakpoint yet (no stopped event received). Ensure the target hits a breakpoint after debug_launch, or use debug_continue and wait for the next stop.");
        return (client, threadId);
    }

    internal static Task WithRetryVoidAsync(Func<Task> action) =>
        DapShared.WithRetryVoidAsync(action);

    internal static Task<T> WithRetryAsync<T>(Func<Task<T>> action) =>
        DapShared.WithRetryAsync(action);

    internal static string ResolveBreakpointFilePath(string workspaceRoot, string filePath) =>
        DapShared.ResolveBreakpointFilePath(workspaceRoot, filePath);

    internal static string FormatException(Exception ex)
    {
        var msg = ex.Message;
        if (ex.InnerException != null)
            msg += "\nInner: " + ex.InnerException.Message;
        msg += "\n" + ex.StackTrace;
        return msg;
    }
}
