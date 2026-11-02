using System.Text.Json;

namespace Jellyfin.Plugin.OnlineCinema.Services;

internal static class JsonHelpers
{
    internal static string Str(this JsonElement el, string property)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(property, out var p)) return string.Empty;
        return p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;
    }

    internal static int Int(this JsonElement el, string property)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(property, out var p)) return 0;
        return p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : 0;
    }

    internal static long Long(this JsonElement el, string property)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(property, out var p)) return 0;
        return p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n) ? n : 0;
    }

    internal static double Double(this JsonElement el, string property)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(property, out var p)) return 0;
        return p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n) ? n : 0;
    }

    internal static JsonElement Prop(this JsonElement el, string property)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var p) ? p : default;

    internal static IEnumerable<JsonElement> Elements(this JsonElement el)
        => el.ValueKind == JsonValueKind.Array ? el.EnumerateArray() : [];
}
