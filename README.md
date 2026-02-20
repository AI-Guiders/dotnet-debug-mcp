# DotnetDebugMcp

**Автор:** Дмитрий Каратаев (Dmitry Karataev)

MCP-сервер для отладки .NET (C#) через **DAP** (netcoredbg): брейкпоинты (в т.ч. по имени метода), запуск/attach, стек, переменные, шаги, continue/pause/terminate, evaluate, setVariable/setExpression, exceptionInfo.

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
| **debug_pause** | Приостановить выполнение (DAP pause). Остановить поток без брейкпоинта — например если приложение зациклилось. |
| **debug_terminate** | Завершить отлаживаемый процесс (DAP terminate). Сессия остаётся; для нового запуска — **debug_launch**. |
| **debug_evaluate** | Вычислить выражение C# в контексте кадра (DAP evaluate). Аргументы: **expression**; опционально **frame_index**, **wait_seconds**. |
| **debug_scopes** | Области видимости кадра (DAP scopes): имена и variables_reference. Нужны для **debug_set_variable**. Опционально **frame_index**, **wait_seconds**. |
| **debug_set_variable** | Изменить значение переменной при остановке (DAP setVariable). Аргументы: **variables_reference** (из debug_scopes), **name**, **value**. |
| **debug_set_expression** | Установить значение выражения в кадре (DAP setExpression). Аргументы: **expression**, **value**; опционально **frame_index**, **wait_seconds**. |
| **debug_exception_info** | Детали исключения для текущего потока (DAP exceptionInfo). Вызывать при остановке по исключению. Опционально **wait_seconds**. |
| **debug_set_function_breakpoints** | Брейкпоинты по имени метода (DAP setFunctionBreakpoints). Аргумент **breakpoints**: массив { **name** (, **condition**?). Имя — например `MyNamespace.MyClass.MyMethod` или `Module!Method`. |
| **debug_cancel** | Отменить последний DAP-запрос (DAP cancel). Когда долго выполняется step_over, stack_trace, variables, continue — вызов прервёт запрос в очереди netcoredbg. Опционально **request_id** (seq), иначе отменяется последний отправленный. |

## Рекомендации по отладке

- **Собирай цель в Debug**, не Release: путь к `bin/Debug/net10.0/YourApp.dll` в target_path и при set_breakpoints. Иначе брейкпоинты могут не срабатывать (пути в PDB).
- **Пути:** `file_path` в брейкпоинтах и `target_path` могут быть относительными — они разрешаются относительно **workspace_path**, чтобы совпадать с путями в PDB при сборке из этого каталога.
- **Условный брейкпоинт:** в `debug_set_breakpoints` у каждого брейкпоинта можно указать **condition** — выражение на C# (например `i > 10`, `name == "test"`). Остановка только когда условие истинно; удобно для цикла или часто вызываемого метода.
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
- При вызове **debug_stack_trace**, **debug_variables**, **debug_step_*** без остановки сервер ждёт следующее **stopped** (по умолчанию 5 с; опционально **wait_seconds** в аргументах, 1..120). Если клиент отменяет запрос, возвращается «Request cancelled…» вместо обрыва.
- **Строка с await / state machine:** async компилируется в state machine (кадры вида `MoveNext`, `d__N`). Сервер это учитывает: (1) после **step_over** / **step_into** / **step_out** следующий вызов **debug_stack_trace** или **debug_variables** без явного `wait_seconds` использует таймаут **15 с** вместо 5; (2) в выводе stack_trace при кадре state machine добавляется подсказка. Явный `wait_seconds=30` и больше по-прежнему можно передать для долгих async-вызовов.
- При временных ошибках DAP (например «target is running», 0x80004005) запросы повторяются до 3 раз с паузой 250 ms.
- Событие **continued** сбрасывает «текущий поток»; следующий stack_trace/variables снова ждут **stopped**.

## Отпускание приложения

- Чтобы приложение снова выполнялось после остановки на брейкпоинте — вызывай **debug_continue**. После этого можно снова вызывать stack_trace/variables (сервер будет ждать следующего stopped до 5 с).
- **debug_stop** перед отключением отправляет **continue** целевой программе, чтобы она не оставалась зависшей. После stop сессия завершена; для новой отладки нужен снова **debug_launch**.
- Перед **пересборкой** целевого проекта вызывай **debug_stop**: netcoredbg держит открытыми exe/dll и PDB целевого процесса; пока сессия активна, копирование PDB при сборке может падать (файл занят netcoredbg.exe).
- В глубоких стеках **debug_step_over** / **debug_step_into** иногда могут оставить приложение без ответа; в таком случае вызови **debug_stop** (он сделает continue и отключится) или заверши процесс netcoredbg снаружи. При обрыве netcoredbg сервер сбрасывает сессию (OnConnectionLost) и не падает.
- Если запрос к **debug_stack_trace** / **debug_variables** был отменён (Aborted или «Request cancelled») пока цель выполнялась (например, долгий step_over), netcoredbg продолжает держать целевой процесс. Вызови **debug_stop** для корректного завершения; если клиент не отвечает — заверши процесс netcoredbg вручную (taskkill /F /IM netcoredbg.exe), тогда цель отпустится.

## Планы (что не покрыто)

1. ~~Проверить **debug_step_into** и **debug_step_out**, а также **debug_stop** после step~~ **Проверено:** step_into заходит в вызов (Main → Foo → Bar), step_out возвращает в вызывающий кадр (Bar → Foo → Main). debug_stop после step отрабатывает: continue и dispose без зависания.
2. ~~Прогнать на практике условный брейкпоинт~~ **Проверено:** condition передаётся в DAP, остановка только при истинном условии (например `i == 2` в цикле). При желании — прогнать **program_args** при launch.
3. ~~При желании — таймаут/отмена для DAP-запросов~~ Отмена учтена: при отмене запроса клиентом (CancellationToken) ожидание stopped прерывается, возвращается сообщение вместо «Aborted». При желании — уточнить в доке про параллельные вызовы stack_trace/variables.

## Лицензия

MIT. См. [LICENSE](LICENSE).
