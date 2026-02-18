using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotnetDebugMcp.Services;

/// <summary>Минимальный DAP-клиент: обмен сообщениями с debug adapter (netcoredbg) по stdio. Content-Length + JSON-RPC. Фоновый поток читает события (stopped и т.д.).</summary>
public sealed partial class DapClient : IAsyncDisposable
{
    [GeneratedRegex(@"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContentLengthRegex();
    private readonly Stream _writer;
    private readonly Stream _reader;
    private readonly Process _process;
    private int _requestId;
    private readonly byte[] _buffer = new byte[1024 * 64];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ConcurrentDictionary<int, TaskCompletionSource<DapResponseResult>> _pendingResponses = new();
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;

    private sealed record DapResponseResult(bool Success, string? ErrorMessage, JsonElement? Body);

    /// <summary>Вызывается при получении события от адаптера (например stopped). body — тело события.</summary>
    public Action<string, JsonElement>? OnEvent { get; set; }

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
        var arguments = new Dictionary<string, object?>
        {
            ["program"] = Path.GetFullPath(program),
            ["cwd"] = string.IsNullOrWhiteSpace(cwd) ? Path.GetDirectoryName(Path.GetFullPath(program)) : Path.GetFullPath(cwd!)
        };
        if (args is { Count: > 0 })
            arguments["args"] = args;
        await SendRequestAsync("launch", arguments, cancellationToken).ConfigureAwait(false);
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
        var match = ContentLengthRegex().Match(headerStr);
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
