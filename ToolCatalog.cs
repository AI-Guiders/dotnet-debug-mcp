using System.Text.Json;
using ModelContextProtocol.Protocol;
using Tool = ModelContextProtocol.Protocol.Tool;

namespace DotnetDebugMcp;

/// <summary>Каталог MCP-тулов. Согласован с <c>mcp-tools.manifest.json</c> и <c>docs/MCP-TOOLS.md</c> (генерация: <c>tools/ExportMcpManifest</c>).</summary>
internal static class ToolCatalog
{
    private static JsonElement Schema(object schema) => JsonSerializer.SerializeToElement(schema);

    private static readonly string[] RequiredFileLine = ["file_path", "line"];
    private static readonly string[] RequiredWorkspace = ["workspace_path"];
    private static readonly string[] RequiredWorkspaceTarget = ["workspace_path", "target_path"];
    private static readonly string[] RequiredWorkspaceTargetBreakpoints = ["workspace_path", "target_path", "breakpoints"];

    internal static List<Tool> Build()
    {
        var emptySchema = Schema(new { type = "object", properties = new { } });

        return
        [
            new()
            {
                Name = "man",
                Description =
                    "MCP ops manual for a tool (not shell man). Pass tool=<name> (e.g. debug_launch); omit tool for TOC. Use on first contact / stuck session / before rebuild while debugging — ListTools is capabilities only.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        tool = new
                        {
                            type = "string",
                            description =
                                "Tool name to document (e.g. debug_launch, debug_stop). Omit or empty for table of contents.",
                        },
                    },
                }),
            },
            new()
            {
                Name = "debug_ping",
                Description = "Проверка доступности сервера отладки. Возвращает текущее время и статус.",
                InputSchema = emptySchema
            },
            new()
            {
                Name = "debug_set_breakpoints",
                Description =
                    "Записать брейкпоинты для целевого проекта/exe. Файл .dotnet-debug-mcp-breakpoints.json в каталоге workspace_path. Дальше: передача в DAP при debug_launch.",
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
                                required = RequiredFileLine
                            }
                        }
                    },
                    required = RequiredWorkspaceTargetBreakpoints
                })
            },
            new()
            {
                Name = "debug_list_breakpoints",
                Description =
                    "Показать сохранённые брейкпоинты. По умолчанию — все цели в workspace; можно указать target_path.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        workspace_path = new { type = "string", description = "Каталог, где лежит .dotnet-debug-mcp-breakpoints.json." },
                        target_path = new { type = "string", description = "Опционально. Путь к .csproj или exe — только брейкпоинты этой цели." }
                    },
                    required = RequiredWorkspace
                })
            },
            new()
            {
                Name = "debug_clear_breakpoints",
                Description =
                    "Удалить сохранённые брейкпоинты: для одной цели (target_path) или для всего workspace.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        workspace_path = new { type = "string", description = "Каталог с файлом брейкпоинтов." },
                        target_path = new { type = "string", description = "Опционально. Очистить только эту цель; без указания — очистить все." }
                    },
                    required = RequiredWorkspace
                })
            },
            new()
            {
                Name = "debug_launch",
                Description =
                    "Запустить отладку через netcoredbg (DAP): загрузить сохранённые брейкпоинты для target, запустить программу под отладчиком. Требуется установленный netcoredbg (путь в netcoredbg_path или NETCOREDBG_PATH). Session graph / stop-before-rebuild: man tool=debug_launch.",
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
                    required = RequiredWorkspaceTarget
                })
            },
            new()
            {
                Name = "debug_attach",
                Description =
                    "Подключиться к уже запущенному .NET-процессу по PID (DAP attach). Опционально target_path — загрузить сохранённые брейкпоинты для этого target.",
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
                Description =
                    "Продолжить выполнение после остановки на брейкпоинте (DAP continue). Требуется активная сессия после debug_launch.",
                InputSchema = emptySchema
            },
            new()
            {
                Name = "debug_step_over",
                Description =
                    "Шаг через текущую строку (DAP next). Вызывать только когда выполнение уже остановлено на брейкпоинте (после события stopped). Требуется активная сессия после debug_launch.",
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
                Description =
                    "Шаг из (DAP stepOut): выйти из текущего кадра. Только при остановке на брейкпоинте. Требуется активная сессия.",
                InputSchema = emptySchema
            },
            new()
            {
                Name = "debug_stop",
                Description =
                    "Завершить текущую отладочную сессию (dispose DAP-клиент, освободить ресурсы). После вызова нужен новый debug_launch для отладки.",
                InputSchema = emptySchema
            },
            new()
            {
                Name = "debug_stop_context",
                Description =
                    "После stopped: одним вызовом thread/exception + stack + variables (меньше round-trip). Args как у debug_variables: frame_index, fast, max_depth, max_children_per_node, time_budget_ms, format, json_indented. Inspired by peer MIT stop-context; not a code port.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        frame_index = new { type = "integer", description = "Индекс кадра (0 = верхний). По умолчанию 0." },
                        fast = new { type = "boolean", description = "Быстрый режим (по умолчанию false): max_depth=0 и max_children_per_node=24, если явно не заданы." },
                        time_budget_ms = new { type = "integer", description = "Бюджет времени на раскрытие переменных, 100..10000 мс (по умол. 1800; fast=true => 700)." },
                        max_depth = new { type = "integer", description = "Глубина раскрытия по variablesReference, 0..32, по умол. 4 (или 0 в fast=true)." },
                        max_children_per_node = new { type = "integer", description = "Макс. детей на узел, 1..256, по умол. 48 (или 24 в fast=true)." },
                        format = new { type = "string", description = "text (по умол.) или json — дерево переменных как у debug_variables." },
                        json_indented = new { type = "boolean", description = "Для format=json: многострочный JSON, по умол. true." }
                    },
                    required = Array.Empty<string>()
                })
            },
            new()
            {
                Name = "debug_stack_trace",
                Description =
                    "Стек вызовов текущего потока (DAP stackTrace). Вызывать когда выполнение остановлено на брейкпоинте. Возвращает кадры: имя, файл, строка. Опционально frame_index для debug_variables.",
                InputSchema = emptySchema
            },
            new()
            {
                Name = "debug_variables",
                Description =
                    "Переменные кадра (DAP variables). Когда остановлены. frame_index (0 = верхний) по debug_stack_trace. Для тяжёлых кадров: fast=true, format=json, малый max_depth, затем дети через debug_variable_children. Лимиты: max_depth (0..32, по умол. 4; fast=true => 0), max_children_per_node (1..256, по умол. 48; fast=true => 24), time_budget_ms (100..10000; по умол. 1800, fast=true => 700). При тайм-бюджете ответ помечается partial.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        frame_index = new { type = "integer", description = "Индекс кадра (0 = верхний). По умолчанию 0." },
                        fast = new { type = "boolean", description = "Быстрый режим (по умолчанию false): max_depth=0 и max_children_per_node=24, если явно не заданы." },
                        time_budget_ms = new { type = "integer", description = "Бюджет времени на раскрытие переменных, 100..10000 мс (по умол. 1800; fast=true => 700)." },
                        max_depth = new { type = "integer", description = "Глубина раскрытия по variablesReference, 0..32, по умол. 4 (или 0 в fast=true)." },
                        max_children_per_node = new { type = "integer", description = "Макс. детей на узел, 1..256, по умол. 48 (или 24 в fast=true)." },
                        format = new { type = "string", description = "text (по умол.) или json — дерево с полями name, value, type, variablesReference, children." },
                        json_indented = new { type = "boolean", description = "Для format=json: многострочный JSON, по умол. true." }
                    },
                    required = Array.Empty<string>()
                })
            },
            new()
            {
                Name = "debug_variable_children",
                Description =
                    "Один уровень вложенных переменных по variablesReference (из JSON debug_variables: не рекурсия). Снижает объём ответа. Подсказки indexed_variables / named_variables с родителя; max_children; json_indented.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        variables_reference = new { type = "integer", description = "Ненулевой ref из DAP (поле variablesReference у родителя)." },
                        named_variables = new { type = "integer", description = "Опционально. Подсказка namedVariables родителя для fallback DAP." },
                        indexed_variables = new { type = "integer", description = "Опционально. Подсказка indexedVariables родителя (массивы)." },
                        max_children = new { type = "integer", description = "Макс. число детей, 1..256, по умол. 64." },
                        json_indented = new { type = "boolean", description = "По умол. true." }
                    },
                    required = new[] { "variables_reference" }
                })
            }
        ];
    }
}
