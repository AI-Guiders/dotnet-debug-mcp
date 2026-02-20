using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using DotnetDebugMcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tool = ModelContextProtocol.Protocol.Tool;

// MCP-сервер для отладки .NET: DAP + интеграция с Visual Studio (DTE).
// Сейчас: хранение брейкпоинтов (set/list/clear); DAP launch + continue/next.

static JsonElement Schema(object schema) => JsonSerializer.SerializeToElement(schema);

var emptySchema = Schema(new { type = "object", properties = new { } });

var toolsList = new List<Tool>
{
    new()
    {
        Name = "debug_ping",
        Description = "Проверка доступности сервера отладки. Возвращает текущее время и статус.",
        InputSchema = emptySchema
    },
    new()
    {
        Name = "debug_set_breakpoints",
        Description = "Записать брейкпоинты для целевого проекта/exe. Файл .dotnet-debug-mcp-breakpoints.json в каталоге workspace_path. Дальше: передача в DAP при debug_launch.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог проекта/решения (здесь создаётся файл с брейкпоинтами)." },
                target_path = new { type = "string", description = "Путь к .csproj или exe — ключ для списка брейкпоинтов (при launch будем использовать этот target)." },
                breakpoints = new
                {
                    type = "array",
                    description = "Список брейкпоинтов: file_path, line (1-based), condition (опционально).",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            file_path = new { type = "string" },
                            line = new { type = "integer" },
                            condition = new { type = "string" }
                        },
                        required = s_requiredFileLine }
                }
            },
            required = s_requiredWorkspaceTargetBreakpoints
        })
    },
    new()
    {
        Name = "debug_list_breakpoints",
        Description = "Показать сохранённые брейкпоинты. По умолчанию — все цели в workspace; можно указать target_path.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог, где лежит .dotnet-debug-mcp-breakpoints.json." },
                target_path = new { type = "string", description = "Опционально. Путь к .csproj или exe — только брейкпоинты этой цели." }
            },
            required = s_requiredWorkspace
        })
    },
    new()
    {
        Name = "debug_clear_breakpoints",
        Description = "Удалить сохранённые брейкпоинты: для одной цели (target_path) или для всего workspace.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог с файлом брейкпоинтов." },
                target_path = new { type = "string", description = "Опционально. Очистить только эту цель; без указания — очистить все." }
            },
            required = s_requiredWorkspace
        })
    },
    new()
    {
        Name = "debug_launch",
        Description = "Запустить отладку через netcoredbg (DAP): загрузить сохранённые брейкпоинты для target, запустить программу под отладчиком. Требуется установленный netcoredbg (путь в netcoredbg_path или переменная NETCOREDBG_PATH).",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог с .dotnet-debug-mcp-breakpoints.json." },
                target_path = new { type = "string", description = "Путь к .dll или .exe для запуска под отладчиком (тот же ключ, что при debug_set_breakpoints)." },
                netcoredbg_path = new { type = "string", description = "Опционально. Путь к netcoredbg. По умолчанию: переменная NETCOREDBG_PATH или \"netcoredbg\" из PATH." },
                program_args = new { type = "array", description = "Опционально. Аргументы командной строки для целевой программы (массив строк).", items = new { type = "string" } }
            },
            required = s_requiredWorkspaceTarget
        })
    },
    new()
    {
        Name = "debug_attach",
        Description = "Подключиться к уже запущенному .NET-процессу по PID (DAP attach). Опционально target_path — загрузить сохранённые брейкпоинты для этого target.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог с .dotnet-debug-mcp-breakpoints.json (нужен при указании target_path)." },
                process_id = new { type = "integer", description = "PID процесса .NET, к которому подключаемся." },
                target_path = new { type = "string", description = "Опционально. Путь к .dll/.exe целевого процесса — для загрузки брейкпоинтов из JSON (тот же ключ, что при set_breakpoints)." },
                netcoredbg_path = new { type = "string", description = "Опционально. Путь к netcoredbg." }
            },
            required = new[] { "workspace_path", "process_id" }
        })
    },
    new()
    {
        Name = "debug_continue",
        Description = "Продолжить выполнение после остановки на брейкпоинте (DAP continue). Требуется активная сессия после debug_launch.",
        InputSchema = emptySchema
    },
    new()
    {
        Name = "debug_step_over",
        Description = "Шаг через текущую строку (DAP next). Вызывать только когда выполнение уже остановлено на брейкпоинте (после события stopped). Требуется активная сессия после debug_launch.",
        InputSchema = emptySchema
    },
    new()
    {
        Name = "debug_step_into",
        Description = "Шаг в (DAP stepIn): зайти в вызов. Только при остановке на брейкпоинте. Требуется активная сессия.",
        InputSchema = emptySchema
    },
    new()
    {
        Name = "debug_step_out",
        Description = "Шаг из (DAP stepOut): выйти из текущего кадра. Только при остановке на брейкпоинте. Требуется активная сессия.",
        InputSchema = emptySchema
    },
    new()
    {
        Name = "debug_stop",
        Description = "Завершить текущую отладочную сессию (dispose DAP-клиент, освободить ресурсы). После вызова нужен новый debug_launch для отладки.",
        InputSchema = emptySchema
    },
    new()
    {
        Name = "debug_stack_trace",
        Description = "Стек вызовов текущего потока (DAP stackTrace). Вызывать когда выполнение остановлено на брейкпоинте. Возвращает кадры: имя, файл, строка. Опционально wait_seconds (после step_over на await увеличь, напр. 30).",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                wait_seconds = new { type = "integer", description = "Секунд ожидания остановки (по умолчанию 5). После step_over на строке с await — увеличь (например 30)." }
            },
            required = Array.Empty<string>()
        })
    },
    new()
    {
        Name = "debug_variables",
        Description = "Переменные кадра (DAP variables). Вызывать когда остановлены. Без аргументов — переменные верхнего кадра (frame_index=0). Опционально wait_seconds после step_over на await.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                frame_index = new { type = "integer", description = "Индекс кадра в стеке (0 = верхний). По умолчанию 0." },
                wait_seconds = new { type = "integer", description = "Секунд ожидания остановки (по умолчанию 5). После step_over на строке с await — увеличь (например 30)." }
            },
            required = Array.Empty<string>()
        })
    },
    new()
    {
        Name = "debug_pause",
        Description = "Приостановить выполнение (DAP pause). Остановить поток без брейкпоинта — например если приложение зациклилось. Требуется активная сессия.",
        InputSchema = emptySchema
    },
    new()
    {
        Name = "debug_terminate",
        Description = "Завершить отлаживаемый процесс (DAP terminate). Сессия netcoredbg остаётся; для новой отладки — debug_launch. Требуется активная сессия.",
        InputSchema = emptySchema
    },
    new()
    {
        Name = "debug_evaluate",
        Description = "Вычислить выражение C# в контексте текущего кадра (DAP evaluate). Например: \"x + 1\", \"items.Count\". Опционально frame_index (0 = верхний кадр).",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                expression = new { type = "string", description = "Выражение C# для вычисления." },
                frame_index = new { type = "integer", description = "Индекс кадра (по умолчанию 0)." },
                wait_seconds = new { type = "integer", description = "Секунд ожидания остановки." }
            },
            required = new[] { "expression" }
        })
    },
    new()
    {
        Name = "debug_scopes",
        Description = "Области видимости кадра (DAP scopes): имена и variablesReference. Нужны для debug_set_variable (передать variables_reference области, например Locals). Опционально frame_index.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                frame_index = new { type = "integer", description = "Индекс кадра (по умолчанию 0)." },
                wait_seconds = new { type = "integer", description = "Секунд ожидания остановки." }
            },
            required = Array.Empty<string>()
        })
    },
    new()
    {
        Name = "debug_set_variable",
        Description = "Изменить значение переменной при остановке (DAP setVariable). variables_reference — reference области (из debug_scopes, например Locals). name — имя переменной, value — новое значение (строка C#).",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                variables_reference = new { type = "integer", description = "DAP variablesReference области (из debug_scopes)." },
                name = new { type = "string", description = "Имя переменной." },
                value = new { type = "string", description = "Новое значение (выражение C#)." }
            },
            required = new[] { "variables_reference", "name", "value" }
        })
    },
    new()
    {
        Name = "debug_set_expression",
        Description = "Установить значение выражения в контексте кадра (DAP setExpression). Например expression=\"x\", value=\"10\". Опционально frame_index.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                expression = new { type = "string", description = "Имя переменной или выражение (левая часть)." },
                value = new { type = "string", description = "Новое значение (правая часть)." },
                frame_index = new { type = "integer", description = "Индекс кадра (по умолчанию 0)." },
                wait_seconds = new { type = "integer", description = "Секунд ожидания остановки." }
            },
            required = new[] { "expression", "value" }
        })
    },
    new()
    {
        Name = "debug_exception_info",
        Description = "Детали исключения для текущего потока (DAP exceptionInfo). Вызывать когда остановка по исключению (reason=exception). Возвращает exceptionId, description, details.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                wait_seconds = new { type = "integer", description = "Секунд ожидания остановки." }
            },
            required = Array.Empty<string>()
        })
    },
    new()
    {
        Name = "debug_set_function_breakpoints",
        Description = "Установить брейкпоинты по имени метода (DAP setFunctionBreakpoints). name — полное имя метода, например \"MyNamespace.MyClass.MyMethod\" или \"Module!Method\". Опционально condition для каждого.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                breakpoints = new
                {
                    type = "array",
                    description = "Список: name (обязательно), condition (опционально).",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string" },
                            condition = new { type = "string" }
                        },
                        required = new[] { "name" }
                    }
                }
            },
            required = new[] { "breakpoints" }
        })
    },
    new()
    {
        Name = "debug_cancel",
        Description = "Отменить последний отправленный DAP-запрос (DAP cancel). Использовать, когда долго выполняется step_over, stack_trace, variables, continue и т.д. — netcoredbg прервёт запрос в очереди. Опционально request_id (seq запроса), иначе отменяется последний отправленный.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                request_id = new { type = "integer", description = "Опционально. DAP request_seq для отмены; без указания — отмена последнего отправленного запроса." }
            },
            required = Array.Empty<string>()
        })
    }
};

string HandleSetBreakpoints(IReadOnlyDictionary<string, JsonElement> args)
{
    if (!TryGetString(args, "workspace_path", out var workspacePath) || string.IsNullOrWhiteSpace(workspacePath))
        throw new ArgumentException("workspace_path is required.");
    if (!TryGetString(args, "target_path", out var targetPath) || string.IsNullOrWhiteSpace(targetPath))
        throw new ArgumentException("target_path is required.");
    if (!args.TryGetValue("breakpoints", out var bpEl) || bpEl.ValueKind != JsonValueKind.Array)
        throw new ArgumentException("breakpoints (array) is required.");

    var list = new List<BreakpointsStorage.BreakpointEntry>();
    foreach (var item in bpEl.EnumerateArray())
    {
        if (!TryGetPropString(item, "file_path", out var file) || !TryGetPropInt(item, "line", out var line) || line < 1)
            continue;
        TryGetPropString(item, "condition", out var condition);
        list.Add(new BreakpointsStorage.BreakpointEntry(file!, line, string.IsNullOrWhiteSpace(condition) ? null : condition));
    }

    BreakpointsStorage.SetBreakpoints(workspacePath!, targetPath!, list);
    var sb = new StringBuilder();
    sb.AppendLine("# Breakpoints set");
    sb.AppendLine($"# Target: {targetPath}");
    sb.AppendLine($"# File: {BreakpointsStorage.FileName} in workspace");
    sb.AppendLine($"# Count: {list.Count}");
    foreach (var bp in list)
        sb.AppendLine($"  {bp.File}:{bp.Line}" + (bp.Condition != null ? $" condition=\"{bp.Condition}\"" : ""));
    return sb.ToString();
}

string HandleListBreakpoints(IReadOnlyDictionary<string, JsonElement> args)
{
    if (!TryGetString(args, "workspace_path", out var workspacePath) || string.IsNullOrWhiteSpace(workspacePath))
        throw new ArgumentException("workspace_path is required.");
    TryGetString(args, "target_path", out var targetPath);

    var sb = new StringBuilder();
    sb.AppendLine("# Saved breakpoints");
    if (!string.IsNullOrWhiteSpace(targetPath))
    {
        var bps = BreakpointsStorage.GetBreakpoints(workspacePath!, targetPath);
        sb.AppendLine($"# Target: {targetPath}");
        sb.AppendLine($"# Count: {bps.Count}");
        foreach (var bp in bps)
            sb.AppendLine($"  {bp.File}:{bp.Line}" + (bp.Condition != null ? $" condition=\"{bp.Condition}\"" : ""));
    }
    else
    {
        var targets = BreakpointsStorage.ListTargets(workspacePath!);
        foreach (var (target, bps) in targets)
        {
            sb.AppendLine($"# Target: {target}");
            foreach (var bp in bps)
                sb.AppendLine($"  {bp.File}:{bp.Line}" + (bp.Condition != null ? $" condition=\"{bp.Condition}\"" : ""));
            sb.AppendLine();
        }
    }
    return sb.ToString();
}

string HandleClearBreakpoints(IReadOnlyDictionary<string, JsonElement> args)
{
    if (!TryGetString(args, "workspace_path", out var workspacePath) || string.IsNullOrWhiteSpace(workspacePath))
        throw new ArgumentException("workspace_path is required.");
    TryGetString(args, "target_path", out var targetPath);
    BreakpointsStorage.ClearBreakpoints(workspacePath!, targetPath);
    return string.IsNullOrWhiteSpace(targetPath)
        ? "# All breakpoints cleared for this workspace."
        : $"# Breakpoints cleared for target: {targetPath}";
}

async Task<string> HandleDebugLaunch(IReadOnlyDictionary<string, JsonElement> args)
{
    if (!TryGetString(args, "workspace_path", out var workspacePath) || string.IsNullOrWhiteSpace(workspacePath))
        throw new ArgumentException("workspace_path is required.");
    if (!TryGetString(args, "target_path", out var targetPath) || string.IsNullOrWhiteSpace(targetPath))
        throw new ArgumentException("target_path is required.");

    var netcoredbgPath = Environment.GetEnvironmentVariable("NETCOREDBG_PATH")?.Trim();
    if (TryGetString(args, "netcoredbg_path", out var customPath) && !string.IsNullOrWhiteSpace(customPath))
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
        .GroupBy(b => ResolveBreakpointFilePath(workspaceRoot, b.File))
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
    try
    {
        await client.LaunchAsync(programPath, Path.GetDirectoryName(programPath), programArgs).ConfigureAwait(false);
        foreach (var (file, list) in byFile)
        {
            if (list.Count > 0)
                await client.SetBreakpointsAsync(file, list).ConfigureAwait(false);
        }
        try
        {
            await client.SetExceptionBreakpointsAsync(["unhandled"]).ConfigureAwait(false);
        }
        catch
        {
            // netcoredbg может вернуть ошибку на setExceptionBreakpoints (0x80070057); не ломаем сессию.
        }
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
    sb.AppendLine("# Exception breakpoints: unhandled (stop on throw)");
    if (!stopped)
        sb.AppendLine("# (Wait for breakpoint timed out — call debug_continue then use stack_trace/step_* after it stops, or check that the target hits a breakpoint. First thread id used as fallback if available.)");
    else if (DebugSession.LastExceptionText is { } exMsg)
        sb.AppendLine($"# Stopped on exception: {exMsg}");
    sb.AppendLine("# Use debug_continue or debug_step_over to control execution.");
    return sb.ToString();
}

async Task<string> HandleDebugAttach(IReadOnlyDictionary<string, JsonElement> args)
{
    if (!TryGetString(args, "workspace_path", out var workspacePath) || string.IsNullOrWhiteSpace(workspacePath))
        throw new ArgumentException("workspace_path is required.");
    if (!args.TryGetValue("process_id", out var pidEl) || !pidEl.TryGetInt32(out var processId) || processId <= 0)
        throw new ArgumentException("process_id (positive integer) is required.");

    var netcoredbgPath = Environment.GetEnvironmentVariable("NETCOREDBG_PATH")?.Trim();
    if (TryGetString(args, "netcoredbg_path", out var customPath) && !string.IsNullOrWhiteSpace(customPath))
        netcoredbgPath = customPath;
    if (string.IsNullOrWhiteSpace(netcoredbgPath))
        netcoredbgPath = "netcoredbg";

    var workspaceRoot = Path.GetFullPath(workspacePath!.Trim());
    if (File.Exists(workspaceRoot))
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;

    var breakpoints = new List<BreakpointsStorage.BreakpointEntry>();
    var byFile = new Dictionary<string, List<(int Line, string? Condition)>>();
    if (TryGetString(args, "target_path", out var targetPath) && !string.IsNullOrWhiteSpace(targetPath))
    {
        breakpoints = BreakpointsStorage.GetBreakpoints(workspacePath, targetPath).ToList();
        byFile = breakpoints
            .GroupBy(b => ResolveBreakpointFilePath(workspaceRoot, b.File))
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
    try
    {
        await client.AttachAsync(processId).ConfigureAwait(false);
        foreach (var (file, list) in byFile)
        {
            if (list.Count > 0)
                await client.SetBreakpointsAsync(file, list).ConfigureAwait(false);
        }
        try
        {
            await client.SetExceptionBreakpointsAsync(["unhandled"]).ConfigureAwait(false);
        }
        catch
        {
            // При attach netcoredbg может вернуть ошибку на setExceptionBreakpoints (0x80070057); не ломаем сессию.
        }
        try
        {
            await client.ConfigurationDoneAsync().ConfigureAwait(false);
        }
        catch
        {
            // При attach netcoredbg иногда падает и на configurationDone (0x80070057); продолжаем.
        }
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
    sb.AppendLine("# Exception breakpoints: unhandled (stop on throw)");
    if (!stopped)
        sb.AppendLine("# (Wait for breakpoint timed out — call debug_continue then use stack_trace/step_* after it stops.)");
    else if (DebugSession.LastExceptionText is { } exMsg)
        sb.AppendLine($"# Stopped on exception: {exMsg}");
    sb.AppendLine("# Use debug_continue or debug_step_over to control execution.");
    return sb.ToString();
}

static (DapClient client, int threadId) GetSessionAndThreadId()
{
    var client = DebugSession.CurrentClient
        ?? throw new InvalidOperationException("No active debug session. Run debug_launch first.");
    var threadId = DebugSession.LastStoppedThreadId;
    if (threadId == 0)
        throw new InvalidOperationException("Execution has not stopped on a breakpoint yet (no stopped event received). Ensure the target hits a breakpoint after debug_launch, or use debug_continue and wait for the next stop.");
    return (client, threadId);
}

static bool IsTransientDapError(Exception ex)
{
    var msg = ex.Message;
    return msg.Contains("running", StringComparison.OrdinalIgnoreCase)
        || msg.Contains("0x80004005", StringComparison.Ordinal)
        || msg.Contains("Failed command", StringComparison.OrdinalIgnoreCase);
}

const int DapRetryCount = 3;
const int DapRetryDelayMs = 250;

static async Task WithRetryVoidAsync(Func<Task> action)
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

static async Task<T> WithRetryAsync<T>(Func<Task<T>> action)
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

async Task<string> HandleDebugContinue(IReadOnlyDictionary<string, JsonElement> _)
{
    var (client, threadId) = GetSessionAndThreadId();
    await client.ContinueAsync(threadId).ConfigureAwait(false);
    return "# Continued execution.";
}

async Task<string> HandleDebugStepOver(IReadOnlyDictionary<string, JsonElement> _, CancellationToken ct)
{
    try { await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
    catch (OperationCanceledException) { return "# Request cancelled (target may still be running). Call debug_stack_trace again when stopped."; }
    catch (TimeoutException) { return "# Timeout (5s) waiting for execution to stop."; }
    var (client, threadId) = GetSessionAndThreadId();
    try { await WithRetryVoidAsync(() => client.NextAsync(threadId)).ConfigureAwait(false); }
    catch (InvalidOperationException ex) { return "# " + ex.Message; }
    DebugSession.LastCommandWasStep = true;
    return "# Step over sent; execution will stop at next line. If the current line is an await, execution runs until the async call completes — next stack_trace/variables uses 15s wait by default.";
}

async Task<string> HandleDebugStepInto(IReadOnlyDictionary<string, JsonElement> _, CancellationToken ct)
{
    try { await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
    catch (OperationCanceledException) { return "# Request cancelled (target may still be running). Call debug_stack_trace again when stopped."; }
    catch (TimeoutException) { return "# Timeout (5s) waiting for execution to stop."; }
    var (client, threadId) = GetSessionAndThreadId();
    try { await WithRetryVoidAsync(() => client.StepInAsync(threadId)).ConfigureAwait(false); }
    catch (InvalidOperationException ex) { return "# " + ex.Message; }
    DebugSession.LastCommandWasStep = true;
    return "# Step into sent; execution will stop inside the call. Next stack_trace/variables uses 15s wait by default if needed.";
}

async Task<string> HandleDebugStepOut(IReadOnlyDictionary<string, JsonElement> _, CancellationToken ct)
{
    try { await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
    catch (OperationCanceledException) { return "# Request cancelled (target may still be running). Call debug_stack_trace again when stopped."; }
    catch (TimeoutException) { return "# Timeout (5s) waiting for execution to stop."; }
    var (client, threadId) = GetSessionAndThreadId();
    try { await WithRetryVoidAsync(() => client.StepOutAsync(threadId)).ConfigureAwait(false); }
    catch (InvalidOperationException ex) { return "# " + ex.Message; }
    DebugSession.LastCommandWasStep = true;
    return "# Step out sent; execution will stop at caller. Next stack_trace/variables uses 15s wait by default if needed.";
}

async Task<string> HandleDebugStop(IReadOnlyDictionary<string, JsonElement> _)
{
    var client = DebugSession.CurrentClient;
    if (client == null)
        return "# No active debug session; nothing to stop.";
    var threadId = DebugSession.LastStoppedThreadId;
    DebugSession.CurrentClient = null;
    DebugSession.LastStoppedThreadId = 0;
    DebugSession.LastCommandWasStep = false;
    if (threadId != 0)
        try { await client.ContinueAsync(threadId).ConfigureAwait(false); } catch { /* целевой процесс продолжит выполнение перед отключением */ }
    await client.DisposeAsync().ConfigureAwait(false);
    return "# Debug session stopped; target resumed, client disposed.";
}

async Task<string> HandleDebugStackTrace(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
{
    var waitSec = GetWaitSeconds(args);
    try
    {
        await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(waitSec), ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        return "# Request cancelled (target may still be running). Call debug_stack_trace again when stopped.";
    }
    catch (TimeoutException)
    {
        return $"# Timeout ({waitSec}s) waiting for execution to stop. If you stepped over an await, the target may still be running the async call — call again with wait_seconds=30 or run debug_continue and try after the next break.";
    }
    var (client, threadId) = GetSessionAndThreadId();
    JsonElement? body;
    try
    {
        body = await WithRetryAsync(() => client.StackTraceAsync(threadId)).ConfigureAwait(false);
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
    string? topFrameName = null;
    foreach (var f in frames.EnumerateArray())
    {
        var name = f.TryGetProperty("name", out var n) ? n.GetString() : "?";
        if (i == 0) topFrameName = name;
        var line = f.TryGetProperty("line", out var ln) ? ln.GetInt32() : 0;
        var path = "";
        if (f.TryGetProperty("source", out var src) && src.TryGetProperty("path", out var p))
            path = p.GetString() ?? "";
        var id = f.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
        sb.AppendLine($"  [{i}] {name} — {path}:{line} (id={id})");
        i++;
    }
    if (topFrameName != null && (topFrameName.Contains("MoveNext", StringComparison.Ordinal) || topFrameName.Contains("d__", StringComparison.Ordinal)))
        sb.AppendLine("# (Async state machine; next step_over may run until await completes — 15s wait is used by default after step.)");
    return sb.ToString();
}

async Task<string> HandleDebugVariables(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
{
    var waitSec = GetWaitSeconds(args);
    try
    {
        await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(waitSec), ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        return "# Request cancelled (target may still be running). Call debug_variables again when stopped.";
    }
    catch (TimeoutException)
    {
        return $"# Timeout ({waitSec}s) waiting for execution to stop. If you stepped over an await, call again with wait_seconds=30 or run debug_continue and try after the next break.";
    }
    var (client, threadId) = GetSessionAndThreadId();
    var frameIndex = 0;
    if (args.TryGetValue("frame_index", out var fiEl) && fiEl.ValueKind == JsonValueKind.Number && fiEl.TryGetInt32(out var fi))
        frameIndex = fi;
    JsonElement? stackBody;
    try
    {
        stackBody = await WithRetryAsync(() => client.StackTraceAsync(threadId)).ConfigureAwait(false);
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
        var scopesBody = await WithRetryAsync(() => client.ScopesAsync(frameId)).ConfigureAwait(false);
        if (scopesBody != null && scopesBody.Value.TryGetProperty("scopes", out var scopesArr))
        {
            foreach (var scope in scopesArr.EnumerateArray())
            {
                if (!scope.TryGetProperty("variablesReference", out var vrefEl) || !vrefEl.TryGetInt32(out var vref) || vref == 0)
                    continue;
                var scopeName = scope.TryGetProperty("name", out var sn) ? sn.GetString() : "?";
                var varsBody = await WithRetryAsync(() => client.VariablesAsync(vref)).ConfigureAwait(false);
                if (varsBody == null || !varsBody.Value.TryGetProperty("variables", out var vars))
                    continue;
                usedScopes = true;
                sb.AppendLine($"## {scopeName}");
                foreach (var v in vars.EnumerateArray())
                {
                    var name = v.TryGetProperty("name", out var n) ? n.GetString() : "?";
                    var value = v.TryGetProperty("value", out var val) ? val.GetString() : "?";
                    var type = v.TryGetProperty("type", out var t) ? t.GetString() : null;
                    sb.AppendLine($"  {name} = {value}" + (type != null ? $" ({type})" : ""));
                }
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
            varsBody = await WithRetryAsync(() => client.VariablesAsync(frameId)).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return "# " + ex.Message;
        }
        if (varsBody == null || !varsBody.Value.TryGetProperty("variables", out var vars))
            return "# No variables for this frame (tried scopes and direct variables).";
        foreach (var v in vars.EnumerateArray())
        {
            var name = v.TryGetProperty("name", out var n) ? n.GetString() : "?";
            var value = v.TryGetProperty("value", out var val) ? val.GetString() : "?";
            var type = v.TryGetProperty("type", out var t) ? t.GetString() : null;
            sb.AppendLine($"  {name} = {value}" + (type != null ? $" ({type})" : ""));
        }
    }

    return sb.ToString();
}

async Task<string> HandleDebugCancel(IReadOnlyDictionary<string, JsonElement> args)
{
    var client = DebugSession.CurrentClient ?? throw new InvalidOperationException("No active debug session. Run debug_launch first.");
    var requestId = client.LastSentRequestId;
    if (args.TryGetValue("request_id", out var ridEl) && ridEl.ValueKind == JsonValueKind.Number && ridEl.TryGetInt32(out var rid) && rid > 0)
        requestId = rid;
    if (requestId <= 0)
        return "# Nothing to cancel (no request has been sent yet in this session).";
    var (success, message) = await client.CancelRequestAsync(requestId).ConfigureAwait(false);
    if (success)
        return "# Cancel sent; the request was cancelled or already finished.";
    return "# Cancel sent; adapter reported: " + (message ?? "request not found or not cancellable");
}

async Task<string> HandleDebugPause(IReadOnlyDictionary<string, JsonElement> _)
{
    var client = DebugSession.CurrentClient ?? throw new InvalidOperationException("No active debug session. Run debug_launch first.");
    var threadId = DebugSession.LastStoppedThreadId;
    if (threadId == 0)
    {
        var threadsBody = await client.ThreadsAsync().ConfigureAwait(false);
        if (threadsBody == null || !threadsBody.Value.TryGetProperty("threads", out var threadsArr))
            throw new InvalidOperationException("Could not get threads for pause.");
        var first = threadsArr.EnumerateArray().FirstOrDefault();
        if (!first.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out threadId) || threadId == 0)
            throw new InvalidOperationException("No thread to pause.");
    }
    await client.PauseAsync(threadId).ConfigureAwait(false);
    return "# Pause sent; execution will stop shortly. Then use debug_stack_trace or debug_variables.";
}

async Task<string> HandleDebugTerminate(IReadOnlyDictionary<string, JsonElement> _)
{
    var client = DebugSession.CurrentClient ?? throw new InvalidOperationException("No active debug session. Run debug_launch first.");
    await client.TerminateAsync().ConfigureAwait(false);
    return "# Target process terminated. Session still active; use debug_launch to start a new run.";
}

async Task<string> HandleDebugEvaluate(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
{
    if (!TryGetString(args, "expression", out var expression) || string.IsNullOrWhiteSpace(expression))
        throw new ArgumentException("expression is required.");
    var waitSec = GetWaitSeconds(args);
    try { await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(waitSec), ct).ConfigureAwait(false); }
    catch (OperationCanceledException) { return "# Request cancelled."; }
    catch (TimeoutException) { return $"# Timeout ({waitSec}s)."; }
    var (client, threadId) = GetSessionAndThreadId();
    int? frameId = null;
    if (args.TryGetValue("frame_index", out var fiEl) && fiEl.ValueKind == JsonValueKind.Number && fiEl.TryGetInt32(out var frameIndex) && frameIndex != 0)
    {
        var stackBody = await WithRetryAsync(() => client.StackTraceAsync(threadId)).ConfigureAwait(false);
        if (stackBody != null && stackBody.Value.TryGetProperty("stackFrames", out var frames))
        {
            var frameList = frames.EnumerateArray().ToList();
            if (frameIndex >= 0 && frameIndex < frameList.Count && frameList[frameIndex].TryGetProperty("id", out var idEl))
                frameId = idEl.GetInt32();
        }
    }
    var body = await client.EvaluateAsync(expression!, frameId).ConfigureAwait(false);
    if (body == null)
        return "# No result.";
    var result = body.Value.TryGetProperty("result", out var r) ? r.GetString() : null;
    var type = body.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
    if (body.Value.TryGetProperty("message", out var msg))
        return "# " + msg.GetString();
    return (result ?? "") + (type != null ? $" ({type})" : "");
}

async Task<string> HandleDebugScopes(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
{
    var waitSec = GetWaitSeconds(args);
    try { await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(waitSec), ct).ConfigureAwait(false); }
    catch (OperationCanceledException) { return "# Request cancelled."; }
    catch (TimeoutException) { return $"# Timeout ({waitSec}s) waiting for stop."; }
    var (client, threadId) = GetSessionAndThreadId();
    var frameIndex = args.TryGetValue("frame_index", out var fiEl) && fiEl.ValueKind == JsonValueKind.Number && fiEl.TryGetInt32(out var fi) ? fi : 0;
    var stackBody = await WithRetryAsync(() => client.StackTraceAsync(threadId)).ConfigureAwait(false);
    if (stackBody == null || !stackBody.Value.TryGetProperty("stackFrames", out var frames))
        return "# No stack.";
    var frameList = frames.EnumerateArray().ToList();
    if (frameIndex < 0 || frameIndex >= frameList.Count)
        return "# frame_index out of range.";
    if (!frameList[frameIndex].TryGetProperty("id", out var idEl))
        return "# Frame has no id.";
    var scopesBody = await WithRetryAsync(() => client.ScopesAsync(idEl.GetInt32())).ConfigureAwait(false);
    if (scopesBody == null || !scopesBody.Value.TryGetProperty("scopes", out var scopes))
        return "# No scopes.";
    var sb = new StringBuilder();
    sb.AppendLine("# Scopes (use variables_reference in debug_set_variable)");
    foreach (var scope in scopes.EnumerateArray())
    {
        var name = scope.TryGetProperty("name", out var n) ? n.GetString() : "?";
        var vref = scope.TryGetProperty("variablesReference", out var vr) && vr.TryGetInt32(out var v) ? v : 0;
        sb.AppendLine($"  {name}: variables_reference={vref}");
    }
    return sb.ToString();
}

async Task<string> HandleDebugSetVariable(IReadOnlyDictionary<string, JsonElement> args)
{
    if (!args.TryGetValue("variables_reference", out var vrefEl) || !vrefEl.TryGetInt32(out var vref))
        throw new ArgumentException("variables_reference (integer) is required.");
    if (!TryGetString(args, "name", out var name) || string.IsNullOrWhiteSpace(name))
        throw new ArgumentException("name is required.");
    if (!TryGetString(args, "value", out var value))
        throw new ArgumentException("value is required.");
    var (client, _) = GetSessionAndThreadId();
    var body = await client.SetVariableAsync(vref, name!, value!).ConfigureAwait(false);
    if (body == null)
        return "# Set variable sent.";
    var result = body.Value.TryGetProperty("value", out var v) ? v.GetString() : null;
    return "# " + (result ?? "OK");
}

async Task<string> HandleDebugSetExpression(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
{
    if (!TryGetString(args, "expression", out var expression) || string.IsNullOrWhiteSpace(expression))
        throw new ArgumentException("expression is required.");
    if (!TryGetString(args, "value", out var value))
        throw new ArgumentException("value is required.");
    var waitSec = GetWaitSeconds(args);
    try { await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(waitSec), ct).ConfigureAwait(false); }
    catch (OperationCanceledException) { return "# Request cancelled."; }
    catch (TimeoutException) { return $"# Timeout ({waitSec}s)."; }
    var (client, threadId) = GetSessionAndThreadId();
    int? frameId = null;
    if (args.TryGetValue("frame_index", out var fiEl) && fiEl.ValueKind == JsonValueKind.Number && fiEl.TryGetInt32(out var frameIndex) && frameIndex != 0)
    {
        var stackBody = await WithRetryAsync(() => client.StackTraceAsync(threadId)).ConfigureAwait(false);
        if (stackBody != null && stackBody.Value.TryGetProperty("stackFrames", out var frames))
        {
            var frameList = frames.EnumerateArray().ToList();
            if (frameIndex >= 0 && frameIndex < frameList.Count && frameList[frameIndex].TryGetProperty("id", out var idEl))
                frameId = idEl.GetInt32();
        }
    }
    var body = await client.SetExpressionAsync(expression!, value!, frameId).ConfigureAwait(false);
    if (body == null)
        return "# OK";
    if (body.Value.TryGetProperty("message", out var msg))
        return "# " + msg.GetString();
    var result = body.Value.TryGetProperty("value", out var v) ? v.GetString() : null;
    return "# " + (result ?? "OK");
}

async Task<string> HandleDebugExceptionInfo(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
{
    var waitSec = GetWaitSeconds(args);
    try { await DebugSession.WaitForStoppedAsync(TimeSpan.FromSeconds(waitSec), ct).ConfigureAwait(false); }
    catch (OperationCanceledException) { return "# Request cancelled."; }
    catch (TimeoutException) { return $"# Timeout ({waitSec}s)."; }
    var (client, threadId) = GetSessionAndThreadId();
    var body = await client.ExceptionInfoAsync(threadId).ConfigureAwait(false);
    if (body == null || !body.Value.TryGetProperty("description", out var desc))
        return "# No exception info (or not stopped on exception).";
    var sb = new StringBuilder();
    sb.AppendLine("# Exception info");
    sb.AppendLine("description: " + desc.GetString());
    if (body.Value.TryGetProperty("exceptionId", out var eid))
        sb.AppendLine("exceptionId: " + eid.GetString());
    if (body.Value.TryGetProperty("breakMode", out var bm))
        sb.AppendLine("breakMode: " + bm.GetString());
    if (body.Value.TryGetProperty("details", out var details))
        sb.AppendLine("details: " + details.GetRawText());
    return sb.ToString();
}

async Task<string> HandleDebugSetFunctionBreakpoints(IReadOnlyDictionary<string, JsonElement> args)
{
    var client = DebugSession.CurrentClient ?? throw new InvalidOperationException("No active debug session. Run debug_launch first.");
    if (!args.TryGetValue("breakpoints", out var bpEl) || bpEl.ValueKind != JsonValueKind.Array)
        throw new ArgumentException("breakpoints (array of { name, condition? }) is required.");
    var list = new List<(string Name, string? Condition)>();
    foreach (var item in bpEl.EnumerateArray())
    {
        if (!TryGetPropString(item, "name", out var name) || string.IsNullOrWhiteSpace(name))
            continue;
        TryGetPropString(item, "condition", out var condition);
        list.Add((name!, string.IsNullOrWhiteSpace(condition) ? null : condition));
    }
    await client.SetFunctionBreakpointsAsync(list).ConfigureAwait(false);
    return $"# Function breakpoints set: {list.Count}.";
}

static string FormatException(Exception ex)
{
    var msg = ex.Message;
    if (ex.InnerException != null)
        msg += "\nInner: " + ex.InnerException.Message;
    msg += "\n" + ex.StackTrace;
    return msg;
}

/// <summary>Путь к исходнику для DAP: совпадает с путями в PDB при сборке из workspace.</summary>
static string ResolveBreakpointFilePath(string workspaceRoot, string filePath)
{
    if (string.IsNullOrWhiteSpace(filePath))
        return Path.GetFullPath(filePath);
    var trimmed = filePath.Trim();
    if (Path.IsPathRooted(trimmed))
        return Path.GetFullPath(trimmed);
    return Path.GetFullPath(Path.Combine(workspaceRoot, trimmed));
}

/// <summary>Читает wait_seconds из аргументов (1..120). По умолчанию 5; если последней была step_* (state machine/await) — 15, если явно не передан wait_seconds.</summary>
static int GetWaitSeconds(IReadOnlyDictionary<string, JsonElement> args)
{
    if (args.TryGetValue("wait_seconds", out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var sec))
        return Math.Clamp(sec, 1, 120);
    if (DebugSession.LastCommandWasStep)
    {
        DebugSession.LastCommandWasStep = false;
        return 15;
    }
    return 5;
}

bool TryGetString(IReadOnlyDictionary<string, JsonElement> args, string key, out string? value)
{
    value = null;
    if (!args.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String)
        return false;
    value = el.GetString();
    return true;
}

bool TryGetPropString(JsonElement el, string key, out string? value)
{
    value = null;
    if (!el.TryGetProperty(key, out var prop) || prop.ValueKind != JsonValueKind.String)
        return false;
    value = prop.GetString();
    return true;
}

bool TryGetPropInt(JsonElement el, string key, out int value)
{
    value = 0;
    if (!el.TryGetProperty(key, out var prop) || prop.ValueKind != JsonValueKind.Number)
        return false;
    return prop.TryGetInt32(out value);
}

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "DotnetDebugMcp", Version = "0.2.0" },
    ProtocolVersion = "2024-11-05",
    Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } },
    Handlers = new McpServerHandlers
    {
        ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = toolsList }),

        CallToolHandler = async (request, cancellationToken) =>
        {
            var name = request.Params?.Name ?? "";
            var args = request.Params?.Arguments is IReadOnlyDictionary<string, JsonElement> a
                ? a
                : FrozenDictionary<string, JsonElement>.Empty;
            try
            {
                string text = name switch
                {
                    "debug_ping" => $"OK {DateTime.UtcNow:O} — DotnetDebugMcp. Tools: {string.Join(", ", toolsList.Select(t => t.Name))}.",
                    "debug_set_breakpoints" => HandleSetBreakpoints(args),
                    "debug_list_breakpoints" => HandleListBreakpoints(args),
                    "debug_clear_breakpoints" => HandleClearBreakpoints(args),
                    "debug_launch" => await HandleDebugLaunch(args),
                    "debug_attach" => await HandleDebugAttach(args),
                    "debug_continue" => await HandleDebugContinue(args),
                    "debug_step_over" => await HandleDebugStepOver(args, cancellationToken),
                    "debug_step_into" => await HandleDebugStepInto(args, cancellationToken),
                    "debug_step_out" => await HandleDebugStepOut(args, cancellationToken),
                    "debug_stop" => await HandleDebugStop(args),
                    "debug_stack_trace" => await HandleDebugStackTrace(args, cancellationToken),
                    "debug_variables" => await HandleDebugVariables(args, cancellationToken),
                    "debug_pause" => await HandleDebugPause(args),
                    "debug_terminate" => await HandleDebugTerminate(args),
                    "debug_evaluate" => await HandleDebugEvaluate(args, cancellationToken),
                    "debug_scopes" => await HandleDebugScopes(args, cancellationToken),
                    "debug_set_variable" => await HandleDebugSetVariable(args),
                    "debug_set_expression" => await HandleDebugSetExpression(args, cancellationToken),
                    "debug_exception_info" => await HandleDebugExceptionInfo(args, cancellationToken),
                    "debug_set_function_breakpoints" => await HandleDebugSetFunctionBreakpoints(args),
                    "debug_cancel" => await HandleDebugCancel(args),
                    _ => throw new ArgumentException($"Unknown tool: {name}.")
                };
                return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
            }
            catch (ArgumentException ex)
            {
                return new CallToolResult { Content = [new TextContentBlock { Text = $"Error: {ex.Message}" }], IsError = true };
            }
            catch (Exception ex)
            {
                return new CallToolResult { Content = [new TextContentBlock { Text = "Error: " + FormatException(ex) }], IsError = true };
            }
        }
    }
};

var transport = new StdioServerTransport("DotnetDebugMcp");
await using var server = McpServer.Create(transport, options);
await server.RunAsync();
return 0;

/// <summary>Текущая активная отладочная сессия после debug_launch. Очередь ожидания stopped + ретраи в тулах.</summary>
static class DebugSession
{
    public static DapClient? CurrentClient { get; set; }
    public static int LastStoppedThreadId { get; set; }
    /// <summary>Текст последнего исключения при остановке по reason=exception (для вывода агенту).</summary>
    public static string? LastExceptionText { get; set; }
    /// <summary>True после step_over/step_into/step_out — следующий stack_trace/variables без явного wait_seconds использует увеличенный таймаут (state machine/await).</summary>
    public static bool LastCommandWasStep { get; set; }

    private static TaskCompletionSource? _currentStoppedTcs;
    private static readonly object StoppedLock = new();

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
        LastCommandWasStep = false;
    }

    /// <summary>Ждать следующего события stopped (или вернуться сразу, если уже paused). Таймаут — TimeoutException, отмена клиента — OperationCanceledException.</summary>
    public static async Task WaitForStoppedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
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
        await waitTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }
}

partial class Program
{
    private static readonly string[] s_requiredFileLine = ["file_path", "line"];
    private static readonly string[] s_requiredWorkspace = ["workspace_path"];
    private static readonly string[] s_requiredWorkspaceTarget = ["workspace_path", "target_path"];
    private static readonly string[] s_requiredWorkspaceTargetBreakpoints = ["workspace_path", "target_path", "breakpoints"];
}
