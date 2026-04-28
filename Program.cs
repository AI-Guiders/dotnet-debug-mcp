using System.Collections.Frozen;
using System.Text.Json;
using DotnetDebugMcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// MCP-сервер для отладки .NET: DAP + интеграция с Visual Studio (DTE).
// Сейчас: хранение брейкпоинтов (set/list/clear); DAP launch + continue/next.

var toolsList = ToolCatalog.Build();

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "DotnetDebugMcp", Version = "0.3.0" },
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
                cancellationToken.ThrowIfCancellationRequested();
                string text = name switch
                {
                    "debug_ping" => $"OK {DateTime.UtcNow:O} — DotnetDebugMcp. Tools: {string.Join(", ", toolsList.Select(t => t.Name))}.",
                    "debug_set_breakpoints" => BreakpointToolHandlers.HandleSetBreakpoints(args),
                    "debug_list_breakpoints" => BreakpointToolHandlers.HandleListBreakpoints(args),
                    "debug_clear_breakpoints" => BreakpointToolHandlers.HandleClearBreakpoints(args),
                    "debug_launch" => await DebugLaunchToolHandlers.HandleDebugLaunch(args),
                    "debug_attach" => await DebugLaunchToolHandlers.HandleDebugAttach(args),
                    "debug_continue" => await DebugControlToolHandlers.HandleDebugContinue(args),
                    "debug_step_over" => await DebugControlToolHandlers.HandleDebugStepOver(args),
                    "debug_step_into" => await DebugControlToolHandlers.HandleDebugStepInto(args),
                    "debug_step_out" => await DebugControlToolHandlers.HandleDebugStepOut(args),
                    "debug_stop" => await DebugControlToolHandlers.HandleDebugStop(args),
                    "debug_stack_trace" => await DebugControlToolHandlers.HandleDebugStackTrace(args),
                    "debug_variables" => await DebugControlToolHandlers.HandleDebugVariables(args),
                    "debug_variable_children" => await DebugControlToolHandlers.HandleDebugVariableChildren(args),
                    _ => throw new ArgumentException($"Unknown tool: {name}.")
                };
                return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
            }
            catch (ArgumentException ex)
            {
                return new CallToolResult { Content = [new TextContentBlock { Text = $"Error: {ex.Message}" }], IsError = true };
            }
            catch (OperationCanceledException)
            {
                // Хост MCP отменил вызов (таймаут, закрытие сессии) — не смешиваем с сбоями DAP/netcoredbg.
                var tag = string.IsNullOrEmpty(name) ? "(unknown)" : name;
                return new CallToolResult { Content = [new TextContentBlock { Text = $"# Aborted: {tag}" }] };
            }
            catch (Exception ex)
            {
                return new CallToolResult { Content = [new TextContentBlock { Text = "Error: " + DapHelpers.FormatException(ex) }], IsError = true };
            }
        }
    }
};

var transport = new StdioServerTransport("DotnetDebugMcp");
await using var server = McpServer.Create(transport, options);
await server.RunAsync();
return 0;
