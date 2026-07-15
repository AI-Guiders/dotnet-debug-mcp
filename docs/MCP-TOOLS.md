# Dotnet Debug MCP — каталог тулов

<!-- GENERATED:ToolCatalog START -->

> Автогенерация из `ToolCatalog.Build()`. Не править этот блок вручную.
>
> Обновление: из каталога `dotnet-debug-mcp` выполнить `dotnet run --project tools/ExportMcpManifest -- --write`.
>
> Тексты совпадают с полем `description` у инструментов MCP; полная схема — в `inputSchema`.

### `man`

MCP ops manual for a tool (not shell man). Pass tool=<name> (e.g. debug_launch); omit tool for TOC. Use on first contact / stuck session / before rebuild while debugging — ListTools is capabilities only.

### `debug_ping`

Проверка доступности сервера отладки. Возвращает текущее время и статус.

### `debug_set_breakpoints`

Записать брейкпоинты для целевого проекта/exe. Файл .dotnet-debug-mcp-breakpoints.json в каталоге workspace_path. Дальше: передача в DAP при debug_launch.

### `debug_list_breakpoints`

Показать сохранённые брейкпоинты. По умолчанию — все цели в workspace; можно указать target_path.

### `debug_clear_breakpoints`

Удалить сохранённые брейкпоинты: для одной цели (target_path) или для всего workspace.

### `debug_launch`

Запустить отладку через netcoredbg (DAP): загрузить сохранённые брейкпоинты для target, запустить программу под отладчиком. Требуется установленный netcoredbg (путь в netcoredbg_path или NETCOREDBG_PATH). Session graph / stop-before-rebuild: man tool=debug_launch.

### `debug_attach`

Подключиться к уже запущенному .NET-процессу по PID (DAP attach). Опционально target_path — загрузить сохранённые брейкпоинты для этого target.

### `debug_continue`

Продолжить выполнение после остановки на брейкпоинте (DAP continue). Требуется активная сессия после debug_launch.

### `debug_step_over`

Шаг через текущую строку (DAP next). Вызывать только когда выполнение уже остановлено на брейкпоинте (после события stopped). Требуется активная сессия после debug_launch.

### `debug_step_into`

Шаг в (DAP stepIn): зайти в вызов. Только при остановке на брейкпоинте. Требуется активная сессия.

### `debug_step_out`

Шаг из (DAP stepOut): выйти из текущего кадра. Только при остановке на брейкпоинте. Требуется активная сессия.

### `debug_stop`

Завершить текущую отладочную сессию (dispose DAP-клиент, освободить ресурсы). После вызова нужен новый debug_launch для отладки.

### `debug_stack_trace`

Стек вызовов текущего потока (DAP stackTrace). Вызывать когда выполнение остановлено на брейкпоинте. Возвращает кадры: имя, файл, строка. Опционально frame_index для debug_variables.

### `debug_variables`

Переменные кадра (DAP variables). Когда остановлены. frame_index (0 = верхний) по debug_stack_trace. Для тяжёлых кадров: fast=true, format=json, малый max_depth, затем дети через debug_variable_children. Лимиты: max_depth (0..32, по умол. 4; fast=true => 0), max_children_per_node (1..256, по умол. 48; fast=true => 24), time_budget_ms (100..10000; по умол. 1800, fast=true => 700). При тайм-бюджете ответ помечается partial.

### `debug_variable_children`

Один уровень вложенных переменных по variablesReference (из JSON debug_variables: не рекурсия). Снижает объём ответа. Подсказки indexed_variables / named_variables с родителя; max_children; json_indented.

<!-- GENERATED:ToolCatalog END -->

