using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tool = ModelContextProtocol.Protocol.Tool;

// MCP-сервер для отладки .NET: DAP + интеграция с Visual Studio (DTE).
// Пока — минимальный скелет с одним инструментом-заглушкой.

static JsonElement EmptyObjectSchema() =>
    JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

var toolsList = new List<Tool>
{
    new()
    {
        Name = "debug_ping",
        Description = "Проверка доступности сервера отладки. Возвращает текущее время и статус.",
        InputSchema = EmptyObjectSchema()
    }
};

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "DotnetDebugMcp", Version = "0.1.0" },
    ProtocolVersion = "2024-11-05",
    Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } },
    Handlers = new McpServerHandlers
    {
        ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = toolsList }),

        CallToolHandler = async (request, _) =>
        {
            var name = request.Params?.Name ?? "";
            if (name != "debug_ping")
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Unknown tool: {name}" }],
                    IsError = true
                };
            }
            var text = $"OK {DateTime.UtcNow:O} — DotnetDebugMcp stub. DAP and VS (DTE) integration coming next.";
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = text }]
            };
        }
    }
};

var transport = new StdioServerTransport("DotnetDebugMcp");
await using var server = McpServer.Create(transport, options);
await server.RunAsync();
return 0;
