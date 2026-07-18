using DotnetDebug.Core;

namespace DotnetDebugMcp;

/// <summary>Текущая активная отладочная сессия после debug_launch/attach. Очередь ожидания stopped + ретраи в тулах.</summary>
internal static class DebugSession
{
    public static DapClient? CurrentClient { get; set; }
    public static int LastStoppedThreadId { get; set; }
    /// <summary>Текст последнего исключения при остановке по reason=exception (для вывода агенту).</summary>
    public static string? LastExceptionText { get; set; }
    /// <summary>Workspace из последнего launch/attach (для MCP resources).</summary>
    public static string? WorkspacePath { get; set; }
    /// <summary>Target key из последнего launch/attach (для breakpoints resource).</summary>
    public static string? TargetPath { get; set; }

    private static TaskCompletionSource? _currentStoppedTcs;
    private static readonly object StoppedLock = new();

    /// <summary>Сбросить клиент и метаданные сессии (stop / connection lost).</summary>
    public static void Clear()
    {
        CurrentClient = null;
        LastStoppedThreadId = 0;
        LastExceptionText = null;
        WorkspacePath = null;
        TargetPath = null;
    }

    /// <summary>Вызвать при старте сессии: создаёт TCS для ожидания следующего stopped.</summary>
    public static void PrepareStoppedWait()
    {
        lock (StoppedLock)
        {
            _currentStoppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Вызывается из read loop при событии stopped. Обновляет threadId, при необходимости — текст исключения, и даёт сигнал ожидающим.</summary>
    public static void OnStopped(int threadId, string? exceptionText = null)
    {
        LastStoppedThreadId = threadId;
        LastExceptionText = exceptionText;
        lock (StoppedLock)
        {
            var t = _currentStoppedTcs;
            _currentStoppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            t?.TrySetResult();
        }
    }

    /// <summary>Вызывается при событии continued — следующий stack_trace/step будет ждать нового stopped.</summary>
    public static void OnContinued()
    {
        LastStoppedThreadId = 0;
        LastExceptionText = null;
    }

    /// <summary>Ждать следующего события stopped (или вернуться сразу, если уже paused). Таймаут — исключение TimeoutException.</summary>
    public static async Task WaitForStoppedAsync(TimeSpan timeout)
    {
        if (CurrentClient == null)
            throw new InvalidOperationException("No active debug session. Run debug_launch first.");
        Task waitTask;
        lock (StoppedLock)
        {
            if (LastStoppedThreadId != 0)
                return;
            waitTask = _currentStoppedTcs?.Task ?? Task.CompletedTask;
        }
        await waitTask.WaitAsync(timeout).ConfigureAwait(false);
    }
}
