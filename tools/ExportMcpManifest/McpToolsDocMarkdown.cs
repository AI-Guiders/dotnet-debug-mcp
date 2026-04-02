using System.Text;

namespace ExportMcpManifest;

internal static class McpToolsDocMarkdown
{
    public static string Build(IEnumerable<(string Name, string Description)> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Dotnet Debug MCP — каталог тулов");
        sb.AppendLine();
        sb.AppendLine("<!-- GENERATED:ToolCatalog START -->");
        sb.AppendLine();
        sb.AppendLine("> Автогенерация из `ToolCatalog.Build()`. Не править этот блок вручную.");
        sb.AppendLine(">");
        sb.AppendLine("> Обновление: из каталога `dotnet-debug-mcp` выполнить `dotnet run --project tools/ExportMcpManifest -- --write`.");
        sb.AppendLine(">");
        sb.AppendLine("> Тексты совпадают с полем `description` у инструментов MCP; полная схема — в `inputSchema`.");
        sb.AppendLine();

        foreach (var (name, description) in tools)
        {
            sb.AppendLine($"### `{name}`");
            sb.AppendLine();
            sb.AppendLine(description.TrimEnd());
            sb.AppendLine();
        }

        sb.AppendLine("<!-- GENERATED:ToolCatalog END -->");
        sb.AppendLine();
        return sb.ToString();
    }
}
