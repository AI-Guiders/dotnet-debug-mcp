namespace DotnetDebugMcp.Tests;

public sealed class ManPagesTests
{
    [Fact]
    public void Toc_lists_launch_stop_continue()
    {
        var toc = ManPages.Resolve(null);
        Assert.Contains("debug_launch", toc, StringComparison.Ordinal);
        Assert.Contains("debug_stop", toc, StringComparison.Ordinal);
        Assert.Contains("debug_continue", toc, StringComparison.Ordinal);
        Assert.Contains("not shell", toc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Launch_page_has_session_graph_and_rebuild_rule()
    {
        var page = ManPages.Resolve("debug_launch");
        Assert.Contains("SESSION GRAPH", page, StringComparison.Ordinal);
        Assert.Contains("debug_stop first", page, StringComparison.Ordinal);
        Assert.Contains("taskkill", page, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("debug_stop")]
    [InlineData("debug_continue")]
    [InlineData("debug_attach")]
    [InlineData("debug_set_breakpoints")]
    public void Known_pages_are_non_empty(string tool)
    {
        var page = ManPages.Resolve(tool);
        Assert.StartsWith("NAME", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown man page", page, StringComparison.Ordinal);
    }
}
