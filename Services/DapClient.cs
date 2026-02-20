using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotnetDebugMcp.Services;

/// <summary>Минимальный DAP-клиент: обмен сообщениями с debug adapter (netcoredbg) по stdio. Content-Length + JSON-RPC. Фоновый поток читает события (stopped и т.д.).</summary>
public sealed class DapClient : IAsyncDisposable
{
    private static readonly Regex ContentLengthRegex = new(@"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
    private readonly Stream _writer;
    private readonly Stream _reader;
    private readonly Process _process;
    private int _requestId;
    private volatile int _lastSentRequestId;
    private readonly byte[] _buffer = new byte[1024 * 64];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ConcurrentDictionary<int, TaskCompletionSource<DapResponseResult>> _pendingResponses = new();
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;

    private sealed record DapResponseResult(bool Success, string? ErrorMessage, JsonElement? Body);

    /// <summary>Вызывается при получении события от адаптера (например stopped). body — тело события.</summary>
    public Action<string, JsonElement>? OnEvent { get; set; }

    /// <summary>Вызывается при обрыве связи (netcoredbg завершён снаружи, stream closed). Позволяет сбросить сессию и не ронять MCP.</summary>
    public Action? OnConnectionLost { get; set; }

    /// <summary>Seq последнего отправленного запроса (для DAP cancel).</summary>
    public int LastSentRequestId => _lastSentRequestId;

    private DapClient(Process process, Stream reader, Stream writer)
    {
        _process = process;
        _reader = reader;
        _writer = writer;
    }

    /// <summary>Фоновый цикл: читает сообщения, события отдаёт в OnEvent, ответы — в ожидающий запрос по request_seq.</summary>
    private async Task RunReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var raw = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                if (type == "event" && root.TryGetProperty("event", out var ev) && root.TryGetProperty("body", out var eventBody))
                {
                    OnEvent?.Invoke(ev.GetString() ?? "", eventBody);
                    continue;
                }
                if (type == "response" && root.TryGetProperty("request_seq", out var seqEl))
                {
                    var requestSeq = seqEl.GetInt32();
                    var success = root.TryGetProperty("success", out var succ) && succ.GetBoolean();
                    var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                    JsonElement? body = null;
                    if (root.TryGetProperty("body", out var b))
                        body = b.Clone();
                    var result = new DapResponseResult(success, message, body);
                    if (_pendingResponses.TryRemove(requestSeq, out var tcs))
                        tcs.TrySetResult(result);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsConnectionLost(ex))
        {
            foreach (var kv in _pendingResponses)
                kv.Value.TrySetResult(new DapResponseResult(false, ex.Message, null));
            OnConnectionLost?.Invoke();
        }
    }

    private static bool IsConnectionLost(Exception ex)
    {
        return ex is IOException or EndOfStreamException or ObjectDisposedException
            || (ex is InvalidOperationException && ex.Message.Contains("stream ended", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Запускает netcoredbg с --interpreter=vscode и возвращает клиент для DAP по stdio. Фоновый read loop стартует сразу.</summary>
    public static async Task<DapClient> StartAsync(string netcoredbgPath, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = netcoredbgPath,
            Arguments = "--interpreter=vscode",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start: {netcoredbgPath}");
        var writer = process.StandardInput.BaseStream;
        var reader = process.StandardOutput.BaseStream;
        var client = new DapClient(process, reader, writer);
        client._readLoopCts = new CancellationTokenSource();
        client._readLoopTask = Task.Run(() => client.RunReadLoopAsync(client._readLoopCts.Token), CancellationToken.None);
        await client.SendRequestAsync("initialize", new Dictionary<string, object?>
        {
            ["clientId"] = "dotnet-debug-mcp",
            ["clientName"] = "DotnetDebugMcp",
            ["adapterID"] = "netcoredbg",
            ["pathFormat"] = "path",
            ["linesStartAt1"] = true,
            ["columnsStartAt1"] = true,
            ["supportsVariableType"] = true,
            ["supportsRunInTerminalRequest"] = false
        }, cancellationToken).ConfigureAwait(false);
        return client;
    }

    public async Task SendRequestAsync(string method, object? args, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _requestId);
        _lastSentRequestId = id;
        var tcs = new TaskCompletionSource<DapResponseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponses[id] = tcs;
        try
        {
            var request = new Dictionary<string, object?>
            {
                ["seq"] = id,
                ["type"] = "request",
                ["command"] = method,
                ["arguments"] = args
            };
            var body = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
            var header = Encoding.UTF8.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await _writer.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            var result = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidOperationException($"DAP {method}: {result.ErrorMessage ?? "Unknown error"}");
        }
        finally
        {
            _pendingResponses.TryRemove(id, out _);
        }
    }

    /// <summary>Отправить запрос и вернуть тело ответа (body). Для запросов без body возвращает null.</summary>
    public async Task<JsonElement?> SendRequestWithBodyAsync(string method, object? args, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _requestId);
        _lastSentRequestId = id;
        var tcs = new TaskCompletionSource<DapResponseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponses[id] = tcs;
        try
        {
            var request = new Dictionary<string, object?>
            {
                ["seq"] = id,
                ["type"] = "request",
                ["command"] = method,
                ["arguments"] = args
            };
            var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
            var header = Encoding.UTF8.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
            await _writer.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            var result = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidOperationException($"DAP {method}: {result.ErrorMessage ?? "Unknown error"}");
            return result.Body;
        }
        finally
        {
            _pendingResponses.TryRemove(id, out _);
        }
    }

    /// <summary>Отправить DAP cancel для указанного request_seq. Возвращает true, если адаптер принял отмену (очередь netcoredbg отменила запрос).</summary>
    public async Task<(bool Success, string? Message)> CancelRequestAsync(int requestId, CancellationToken cancellationToken = default)
    {
        if (requestId <= 0)
            return (false, "requestId must be positive.");
        var id = Interlocked.Increment(ref _requestId);
        _lastSentRequestId = id;
        var tcs = new TaskCompletionSource<DapResponseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponses[id] = tcs;
        try
        {
            var request = new Dictionary<string, object?>
            {
                ["seq"] = id,
                ["type"] = "request",
                ["command"] = "cancel",
                ["arguments"] = new Dictionary<string, object?> { ["requestId"] = requestId }
            };
            var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
            var header = Encoding.UTF8.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
            await _writer.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            var result = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return (result.Success, result.ErrorMessage);
        }
        finally
        {
            _pendingResponses.TryRemove(id, out _);
        }
    }

    public async Task ContinueAsync(int threadId, CancellationToken cancellationToken = default)
    {
        await SendRequestAsync("continue", new Dictionary<string, object?> { ["threadId"] = threadId }, cancellationToken).ConfigureAwait(false);
    }

    public async Task NextAsync(int threadId, CancellationToken cancellationToken = default)
    {
        await SendRequestAsync("next", new Dictionary<string, object?> { ["threadId"] = threadId }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StepInAsync(int threadId, CancellationToken cancellationToken = default)
    {
        await SendRequestAsync("stepIn", new Dictionary<string, object?> { ["threadId"] = threadId }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StepOutAsync(int threadId, CancellationToken cancellationToken = default)
    {
        await SendRequestAsync("stepOut", new Dictionary<string, object?> { ["threadId"] = threadId }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DAP pause: приостановить выполнение потока (остановка без брейкпоинта).</summary>
    public async Task PauseAsync(int threadId, CancellationToken cancellationToken = default)
    {
        await SendRequestAsync("pause", new Dictionary<string, object?> { ["threadId"] = threadId }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DAP terminate: завершить отлаживаемый процесс (DisconnectTerminate).</summary>
    public async Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        await SendRequestAsync("terminate", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DAP evaluate: вычислить выражение в контексте кадра. frameId опционален — при отсутствии используется верхний кадр последнего остановленного потока. Возвращает body (result, type, variablesReference и т.д.) или null.</summary>
    public async Task<JsonElement?> EvaluateAsync(string expression, int? frameId = null, CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["expression"] = expression };
        if (frameId.HasValue)
            args["frameId"] = frameId.Value;
        return await SendRequestWithBodyAsync("evaluate", args, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DAP setVariable: изменить значение переменной по variablesReference и имени.</summary>
    public async Task<JsonElement?> SetVariableAsync(int variablesReference, string name, string value, CancellationToken cancellationToken = default)
    {
        return await SendRequestWithBodyAsync("setVariable", new Dictionary<string, object?>
        {
            ["variablesReference"] = variablesReference,
            ["name"] = name,
            ["value"] = value
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DAP setExpression: установить значение выражения в контексте кадра (например "x" = "42"). frameId опционален.</summary>
    public async Task<JsonElement?> SetExpressionAsync(string expression, string value, int? frameId = null, CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["expression"] = expression,
            ["value"] = value
        };
        if (frameId.HasValue)
            args["frameId"] = frameId.Value;
        return await SendRequestWithBodyAsync("setExpression", args, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DAP exceptionInfo: детали исключения для потока (при остановке по exception). Возвращает body (exceptionId, description, breakMode, details).</summary>
    public async Task<JsonElement?> ExceptionInfoAsync(int threadId, CancellationToken cancellationToken = default)
    {
        return await SendRequestWithBodyAsync("exceptionInfo", new Dictionary<string, object?> { ["threadId"] = threadId }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DAP setFunctionBreakpoints: брейкпоинты по имени метода. name — например "MyClass.MyMethod" или "Module!Method".</summary>
    public async Task SetFunctionBreakpointsAsync(IReadOnlyList<(string Name, string? Condition)> breakpoints, CancellationToken cancellationToken = default)
    {
        var bps = breakpoints.Select(b =>
        {
            var d = new Dictionary<string, object?> { ["name"] = b.Name };
            if (!string.IsNullOrEmpty(b.Condition))
                d["condition"] = b.Condition;
            return d;
        }).ToList();
        await SendRequestAsync("setFunctionBreakpoints", new Dictionary<string, object?> { ["breakpoints"] = bps }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DAP stackTrace: стек вызовов по threadId. Возвращает body ответа (stackFrames) или null.</summary>
    public async Task<JsonElement?> StackTraceAsync(int threadId, int startFrame = 0, int levels = 20, CancellationToken cancellationToken = default)
    {
        return await SendRequestWithBodyAsync("stackTrace", new Dictionary<string, object?>
        {
            ["threadId"] = threadId,
            ["startFrame"] = startFrame,
            ["levels"] = levels
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DAP scopes: области видимости для кадра. Возвращает body (scopes) или null. У многих адаптеров переменные доступны только через scope.variablesReference.</summary>
    public async Task<JsonElement?> ScopesAsync(int frameId, CancellationToken cancellationToken = default)
    {
        return await SendRequestWithBodyAsync("scopes", new Dictionary<string, object?>
        {
            ["frameId"] = frameId
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DAP variables: переменные по variablesReference (id кадра или scope из scopes). Возвращает body ответа (variables) или null.</summary>
    public async Task<JsonElement?> VariablesAsync(int variablesReference, CancellationToken cancellationToken = default)
    {
        return await SendRequestWithBodyAsync("variables", new Dictionary<string, object?>
        {
            ["variablesReference"] = variablesReference
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task LaunchAsync(string program, string? cwd = null, IReadOnlyList<string>? args = null, CancellationToken cancellationToken = default)
    {
        var fullProgram = Path.GetFullPath(program);
        var arguments = new Dictionary<string, object?>
        {
            ["program"] = fullProgram,
            ["cwd"] = string.IsNullOrWhiteSpace(cwd) ? Path.GetDirectoryName(fullProgram) ?? fullProgram : Path.GetFullPath(cwd!)
        };
        if (args is { Count: > 0 })
            arguments["args"] = args;
        // Запуск .dll через dotnet, иначе на Windows возможна 0x800700c1 (неверный образ exe)
        if (fullProgram.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            arguments["runtimeExecutable"] = "dotnet";
        await SendRequestAsync("launch", arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DAP attach: подключиться к уже запущенному .NET-процессу по PID.</summary>
    public async Task AttachAsync(int processId, CancellationToken cancellationToken = default)
    {
        await SendRequestAsync("attach", new Dictionary<string, object?>
        {
            ["processId"] = processId
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetBreakpointsAsync(string sourcePath, IReadOnlyList<(int Line, string? Condition)> breakpoints, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(sourcePath);
        var bps = breakpoints.Select(b =>
        {
            var d = new Dictionary<string, object?> { ["line"] = b.Line };
            if (!string.IsNullOrEmpty(b.Condition))
                d["condition"] = b.Condition;
            return d;
        }).ToList();
        await SendRequestAsync("setBreakpoints", new Dictionary<string, object?>
        {
            ["source"] = new Dictionary<string, object?> { ["path"] = path },
            ["breakpoints"] = bps
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DAP setExceptionBreakpoints: остановка при исключениях. filters — например ["unhandled"] или ["all"].</summary>
    public async Task SetExceptionBreakpointsAsync(IReadOnlyList<string> filters, CancellationToken cancellationToken = default)
    {
        await SendRequestAsync("setExceptionBreakpoints", new Dictionary<string, object?>
        {
            ["filters"] = filters
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ConfigurationDoneAsync(CancellationToken cancellationToken = default)
    {
        await SendRequestAsync("configurationDone", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DAP threads: список потоков. Возвращает body (threads) или null. Используется как fallback для получения threadId при отсутствии события stopped.</summary>
    public async Task<JsonElement?> ThreadsAsync(CancellationToken cancellationToken = default)
    {
        return await SendRequestWithBodyAsync("threads", null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var headerBuilder = new List<byte>(256);
        while (true)
        {
            var n = await _reader.ReadAsync(_buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (n == 0)
                throw new InvalidOperationException("DAP: stream ended.");
            headerBuilder.Add(_buffer[0]);
            if (headerBuilder.Count >= 4 &&
                headerBuilder[^4] == '\r' && headerBuilder[^3] == '\n' &&
                headerBuilder[^2] == '\r' && headerBuilder[^1] == '\n')
                break;
        }
        var headerStr = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(headerBuilder));
        var match = ContentLengthRegex.Match(headerStr);
        if (!match.Success)
            throw new InvalidOperationException("DAP: missing Content-Length in response.");
        var length = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await _reader.ReadAsync(body.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new InvalidOperationException("DAP: stream ended before message complete.");
            offset += read;
        }
        return Encoding.UTF8.GetString(body);
    }

    public void DisposeProcess() => _process.Dispose();

    public async ValueTask DisposeAsync()
    {
        _readLoopCts?.Cancel();
        if (_readLoopTask != null)
        {
            try { await _readLoopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _process.Dispose();
    }
}
