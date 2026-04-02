using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDebugMcp;
using ExportMcpManifest;
using McpToolManifest;
using Tool = ModelContextProtocol.Protocol.Tool;

var tools = ToolCatalog.Build();
var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

if (cliArgs.Contains("--write", StringComparer.Ordinal))
{
    var root = Directory.GetCurrentDirectory();
    var jsonPath = Path.Combine(root, "mcp-tools.manifest.json");
    var mdDir = Path.Combine(root, "docs");
    var mdPath = Path.Combine(mdDir, "MCP-TOOLS.md");
    Directory.CreateDirectory(mdDir);

    WriteJsonFile(jsonPath, tools);
    var md = McpToolsDocMarkdown.Build(tools.Select(t => (t.Name!, t.Description!)));
    File.WriteAllText(mdPath, md, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    Console.Error.WriteLine($"Wrote {jsonPath}");
    Console.Error.WriteLine($"Wrote {mdPath}");
    return;
}

if (cliArgs.Contains("--md-only", StringComparer.Ordinal))
{
    Console.WriteLine(McpToolsDocMarkdown.Build(tools.Select(t => (t.Name!, t.Description!))).TrimEnd());
    return;
}

var doc = new McpToolManifestDocument
{
    SchemaVersion = 1,
    McpId = "dotnet-debug-mcp",
    Title = "Dotnet Debug MCP",
    Tools = tools.Select(t => new McpToolManifestTool { Name = t.Name, Description = t.Description }).ToList()
};

Console.WriteLine(JsonSerializer.Serialize(doc, new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
}));

static void WriteJsonFile(string path, List<Tool> tools)
{
    var doc = new McpToolManifestDocument
    {
        SchemaVersion = 1,
        McpId = "dotnet-debug-mcp",
        Title = "Dotnet Debug MCP",
        Tools = tools.Select(t => new McpToolManifestTool { Name = t.Name, Description = t.Description }).ToList()
    };

    File.WriteAllText(
        path,
        JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + Environment.NewLine,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
