# DotnetDebugMcp

**Автор:** Дмитрий Каратаев (Dmitry Karataev)

MCP-сервер для отладки .NET (C#) через **DAP** (netcoredbg): брейкпоинты, запуск, стек, переменные, шаги, continue/stop.

## Требования

- .NET 10
- [netcoredbg](https://github.com/Samsung/netcoredbg); путь в переменной `NETCOREDBG_PATH` или в параметре **netcoredbg_path** при launch.

## Сборка и запуск

```bash
cd dotnet-debug-mcp
dotnet build
dotnet run
```

Сервер работает по **stdio** (MCP-клиент запускает процесс и общается через stdin/stdout).

## Публикация exe (для MCP в Cursor)

```bash
dotnet publish DotnetDebugMcp.csproj -c Release -o publish
```

В конфиге MCP укажи **command** — путь к `DotnetDebugMcp.exe` в папке `publish`, **args** — `[]`.

## Инструменты

| Инструмент | Описание |
|------------|----------|
| **debug_ping** | Проверка доступности сервера. |
| **debug_set_breakpoints** | Записать брейкпоинты: workspace_path, target_path (.dll/.exe), breakpoints[] (file_path, line; опционально condition). Файл `.dotnet-debug-mcp-breakpoints.json` в workspace. |
| **debug_list_breakpoints** | Показать сохранённые брейкпоинты (по workspace, опционально по target_path). |
| **debug_clear_breakpoints** | Удалить брейкпоинты (по workspace или по target_path). |
| **debug_launch** | Запустить отладку: workspace_path, target_path; опционально **netcoredbg_path**, **program_args** (массив строк — аргументы для целевой программы). Загружает брейкпоинты, передаёт в DAP setBreakpoints, ждёт первого события stopped (до 5 с). |
| **debug_attach** | Подключиться к уже запущенному .NET-процессу по **process_id** (PID). workspace_path обязателен; опционально **target_path** (путь к .dll/.exe процесса) — тогда загружаются сохранённые брейкпоинты для этого target. |
| **debug_continue** | Продолжить выполнение (DAP continue). |
| **debug_step_over** | Шаг через строку (DAP next). |
| **debug_step_into** | Шаг в вызов (DAP stepIn). |
| **debug_step_out** | Шаг из кадра (DAP stepOut). |
| **debug_stop** | Завершить сессию (dispose клиента). |
| **debug_stack_trace** | Стек вызовов текущего потока (DAP stackTrace). При необходимости сервер ждёт остановки (до 5 с) и повторяет запрос при временных ошибках. |
| **debug_variables** | Переменные кадра (frame_index=0 по умолчанию). Сначала запрос **scopes** по frameId, затем **variables** по variablesReference каждого scope (netcoredbg отдаёт переменные через scopes); при ошибке — fallback на variables(frameId). |

## Рекомендации по отладке

- **Собирай цель в Debug**, не Release: путь к `bin/Debug/net10.0/YourApp.dll` в target_path и при set_breakpoints. Иначе брейкпоинты могут не срабатывать (пути в PDB).
- **Пути:** `file_path` в брейкпоинтах и `target_path` могут быть относительными — они разрешаются относительно **workspace_path**, чтобы совпадать с путями в PDB при сборке из этого каталога.
- Если целевая программа без аргументов сразу выходит — передай **program_args** (например `["dummy"]`), чтобы выполнение дошло до нужной строки.

## Пример сценария

1. `debug_set_breakpoints` — workspace_path, target_path (путь к .dll), breakpoints: [{ file_path, line: 7 }].
2. `debug_launch` — те же workspace_path, target_path; при необходимости program_args.
3. `debug_stack_trace` → стек.
4. `debug_variables` → переменные (Locals и др. по scopes).
5. `debug_step_over` → следующий шаг.
6. `debug_continue` → продолжить.
7. `debug_stop` → завершить сессию.

## Поведение

- После **debug_launch** сервер ждёт первое событие **stopped** (до 5 с); при таймауте пробует получить threadId через запрос **threads**.
- При вызове **debug_stack_trace**, **debug_variables**, **debug_step_*** без остановки сервер ждёт следующее **stopped** (до 5 с).
- При временных ошибках DAP (например «target is running», 0x80004005) запросы повторяются до 3 раз с паузой 250 ms.
- Событие **continued** сбрасывает «текущий поток»; следующий stack_trace/variables снова ждут **stopped**.

## Отпускание приложения

- Чтобы приложение снова выполнялось после остановки на брейкпоинте — вызывай **debug_continue**. После этого можно снова вызывать stack_trace/variables (сервер будет ждать следующего stopped до 5 с).
- **debug_stop** перед отключением отправляет **continue** целевой программе, чтобы она не оставалась зависшей. После stop сессия завершена; для новой отладки нужен снова **debug_launch**.
- В глубоких стеках **debug_step_over** / **debug_step_into** иногда могут оставить приложение без ответа; в таком случае вызови **debug_stop** (он сделает continue и отключится) или заверши процесс netcoredbg снаружи. При обрыве netcoredbg сервер сбрасывает сессию (OnConnectionLost) и не падает.

## Планы (что не покрыто)

1. Проверить **debug_step_into** и **debug_step_out**, а также **debug_stop** после step.
2. Прогнать условный брейкпоинт (**condition** в set_breakpoints) и **program_args** при launch.
3. При желании — таймаут/отмена для DAP-запросов; уточнить в доке про параллельные вызовы stack_trace/variables.

## Лицензия

MIT. См. [LICENSE](LICENSE).
