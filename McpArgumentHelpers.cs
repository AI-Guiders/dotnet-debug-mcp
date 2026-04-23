using System.Text.Json;

namespace DotnetDebugMcp;

internal static class McpArgumentHelpers
{
    internal static bool TryGetString(IReadOnlyDictionary<string, JsonElement> args, string key, out string? value)
    {
        value = null;
        if (!args.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String)
            return false;
        value = el.GetString();
        return true;
    }

    internal static bool TryGetPropString(JsonElement el, string key, out string? value)
    {
        value = null;
        if (!el.TryGetProperty(key, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;
        value = prop.GetString();
        return true;
    }

    internal static bool TryGetPropInt(JsonElement el, string key, out int value)
    {
        value = 0;
        if (!el.TryGetProperty(key, out var prop) || prop.ValueKind != JsonValueKind.Number)
            return false;
        return prop.TryGetInt32(out value);
    }

    /// <summary>Опциональное целое из аргументов MCP; вне [min, max] — зажать.</summary>
    internal static int GetOptionalClampedInt32(
        IReadOnlyDictionary<string, JsonElement> args,
        string key,
        int defaultValue,
        int min,
        int max)
    {
        if (!args.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            return defaultValue;
        if (v < min)
            return min;
        if (v > max)
            return max;
        return v;
    }
}
