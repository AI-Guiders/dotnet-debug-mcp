using McpToolManifest;
using DotnetDebugMcp;

var tools = ToolCatalog.Build().Select(t => (t.Name!, (string?)t.Description)).ToList();
return McpToolManifestExporter.Run(
    args,
    tools,
    new McpToolManifestExportOptions
    {
        McpId = "dotnet-debug-mcp",
        Title = "Dotnet Debug MCP",
        RepoFolderName = "dotnet-debug-mcp",
    });
