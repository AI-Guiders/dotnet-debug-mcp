using DotnetDebugMcp;
using McpToolManifest;

namespace DotnetDebugMcp.Tests;

public sealed class McpToolManifestTests
{
    [Fact]
    public void Mcp_tools_manifest_matches_ToolCatalog_names_and_descriptions()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "mcp-tools.manifest.json");
        Assert.True(File.Exists(manifestPath), $"Missing copied manifest: {manifestPath}");

        var doc = McpToolManifestReader.Load(manifestPath);
        var validation = McpToolManifestReader.Validate(doc);
        Assert.True(validation.Count == 0, string.Join(Environment.NewLine, validation));

        Assert.Equal("dotnet-debug-mcp", doc.McpId);

        var catalog = ToolCatalog.Build();
        var manifestNames = doc.Tools.Select(t => t.Name).ToArray();
        var catalogNames = catalog.Select(t => t.Name).ToArray();
        var nameDiff = ToolCatalogNameComparer.Compare(manifestNames, catalogNames);
        Assert.True(nameDiff.Count == 0, string.Join(Environment.NewLine, nameDiff));

        var catalogByName = catalog.ToDictionary(t => t.Name, t => t.Description, StringComparer.Ordinal);
        Assert.Equal(catalogByName.Count, doc.Tools.Count);
        foreach (var tool in doc.Tools)
        {
            Assert.True(catalogByName.TryGetValue(tool.Name, out var expected), $"Unexpected tool in manifest: {tool.Name}");
            Assert.Equal(expected, tool.Description);
        }
    }
}
