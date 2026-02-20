# DAP: важность vs поддержка netcoredbg

По исходникам netcoredbg (Samsung/netcoredbg, `src/protocols/vscodeprotocol.cpp`).  
**У нас в dotnet-debug-mcp:** используется только подмножество (launch, attach, setBreakpoints, setExceptionBreakpoints, configurationDone, threads, stackTrace, scopes, variables, continue, next, stepIn, stepOut).

| Запрос DAP | Важность для агента | Необходимость в сценариях | netcoredbg |
|------------|----------------------|---------------------------|------------|
| **pause** | Высокая | Остановить выполнение без брейкпоинта (зациклилось) | ✅ Да |
| **terminate** | Высокая | Завершить отлаживаемый процесс | ✅ Да |
| **evaluate** | Высокая | Выполнить выражение в контексте кадра | ✅ Да |
| **setVariable** | Высокая | Изменить значение переменной при остановке | ✅ Да |
| **setExpression** | Высокая | То же, что evaluate/setVariable в другом формате | ✅ Да |
| **cancel** | Высокая | Отменить долгий запрос (ожидание после step) | ✅ Да (очередь команд) |
| **restart** | Средняя | Перезапуск без нового launch | ❌ Нет |
| **exceptionInfo** | Средняя | Детали исключения при остановке по exception | ✅ Да |
| **setFunctionBreakpoints** | Средняя | Брейкпоинты по имени метода | ✅ Да |
| **loadedSources** | Низкая | Список загруженных исходников | ❌ Нет |
| **source** | Низкая | Содержимое исходника по ссылке | ❌ Нет |
| **completions** | Низкая | Автодополнение (консоль/выражения) | ❌ Нет |
| **gotoTargets** | Низкая | Куда можно перейти (goto) | ❌ Нет |
| **dataBreakpointInfo** / **setDataBreakpoints** | Низкая | Брейкпоинт при изменении памяти/переменной | ❌ Нет |
| **readMemory** / **writeMemory** | Низкая | Низкоуровневая отладка | ❌ Нет |
| **disassemble** | Низкая | Дизассемблирование | ❌ Нет |
| **runInTerminal** | Низкая | Запуск в терминале (у нас launch есть) | ❌ Нет |
| **reverseContinue** / **stepBack** | Низкая | Обратное выполнение (time-travel) | ❌ Нет |

## Уже используем в dotnet-debug-mcp

| Запрос | Использование |
|--------|----------------|
| initialize, launch, attach | Запуск/подключение |
| setBreakpoints, setExceptionBreakpoints, configurationDone | Брейкпоинты |
| threads, stackTrace, scopes, variables | Стек и переменные |
| continue, next, stepIn, stepOut | Шаги и продолжение |

## Рекомендация

Сначала добавить в MCP тулы для запросов, которые **важны и уже есть в netcoredbg**:

1. **pause** — `debug_pause` (остановить выполнение).
2. **terminate** — `debug_terminate` (убить отлаживаемый процесс, не закрывая сессию netcoredbg по желанию можно отдельно).
3. **evaluate** — `debug_evaluate` (expression, опционально frame_index).
4. **setVariable** — `debug_set_variable` (variablesReference, name, value).
5. **cancel** — у нас уже учёт CancellationToken при ожидании stopped; при необходимости можно пробрасывать DAP cancel по request_seq для долгих запросов к netcoredbg.

**exceptionInfo** — можно вызывать при остановке по exception и отдавать агенту в ответе (частично уже есть через LastExceptionText из тела события stopped; при необходимости расширить).

**restart** — в netcoredbg нет; при необходимости перезапуск = disconnect + новый launch.
