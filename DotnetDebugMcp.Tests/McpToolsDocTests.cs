using DotnetDebugMcp;

namespace DotnetDebugMcp.Tests;

public sealed class McpToolsDocTests
{
    [Fact]
    public void Mcp_tools_md_matches_ToolCatalog_headings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "docs", "MCP-TOOLS.md");
        Assert.True(File.Exists(path), $"Missing copied doc: {path}");

        var text = File.ReadAllText(path);
        Assert.Contains("<!-- GENERATED:ToolCatalog START -->", text);
        Assert.Contains("<!-- GENERATED:ToolCatalog END -->", text);

        foreach (var tool in ToolCatalog.Build())
        {
            Assert.Contains($"### `{tool.Name}`", text);
            Assert.False(string.IsNullOrEmpty(tool.Description));
            Assert.Contains(tool.Description!, text);
        }
    }
}
