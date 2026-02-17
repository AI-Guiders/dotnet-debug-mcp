# DotnetDebugMcp

MCP-сервер для отладки .NET (C#): управление отладкой через DAP и интеграция с Visual Studio (DTE). Safe-by-default: по умолчанию разрешены breakpoints/step/stack/variables; `evaluate` и `setVariable` — только по явному `unsafe: true`.

**Текущий статус:** скелет (один инструмент-заглушка `debug_ping`). Дальше: DAP-клиент + netcoredbg, затем DTE для брейкпоинтов в VS.

## Требования

- .NET 10

## Сборка и запуск

```bash
cd dotnet-debug-mcp
dotnet build
dotnet run
```

Сервер работает по **stdio** (MCP-клиент запускает процесс и общается через stdin/stdout).

## Публикация exe (для MCP в Cursor/IDE)

```bash
dotnet publish -c Release -r win-x64 --self-contained -o publish
```

В конфиге MCP укажи **command** — путь к `DotnetDebugMcp.exe` в папке `publish`.

## Дальнейшие инструменты (план)

- DAP: `debug_launch`, `debug_attach`, `debug_set_breakpoints`, `debug_continue`, `debug_step_over`, `debug_stack_trace`, `debug_variables`, …
- DTE: `vs_set_breakpoint` (добавить брейкпоинт в открытую Visual Studio).

## Лицензия

Планируется open source (лицензия уточняется).
