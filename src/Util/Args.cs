using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcpBridge;

/// <summary>Typed access to the JSON arguments of a tool call; errors become "bad_request".</summary>
internal static class Args
{
    public static string? Str(JsonObject a, string name, bool required = false)
    {
        var n = a[name];
        if (n == null) return required ? throw Missing(name) : null;
        if (n is JsonValue v && v.TryGetValue(out string? s)) return s;
        throw Bad(name, "a string");
    }

    public static int Int(JsonObject a, string name, int fallback, int min = int.MinValue, int max = int.MaxValue)
    {
        var n = a[name];
        if (n == null) return fallback;
        if (n is JsonValue v && v.TryGetValue(out double d) && d == Math.Floor(d) && d >= min && d <= max) return (int)d;
        throw Bad(name, $"an integer from {min} to {max}");
    }

    public static double? Num(JsonObject a, string name, bool required = false)
    {
        var n = a[name];
        if (n == null) return required ? throw Missing(name) : null;
        if (n is JsonValue v && v.TryGetValue(out double d)) return d;
        throw Bad(name, "a number");
    }

    public static bool Bool(JsonObject a, string name, bool fallback = false)
    {
        var n = a[name];
        if (n == null) return fallback;
        if (n is JsonValue v && v.TryGetValue(out bool b)) return b;
        throw Bad(name, "true or false");
    }

    /// <summary>[x, y] or [x, y, z] in WCS.</summary>
    public static Point3d? Point(JsonObject a, string name, bool required = false)
    {
        var n = a[name];
        if (n == null) return required ? throw Missing(name) : null;
        return ToPoint(n, name);
    }

    public static Point3d ToPoint(JsonNode n, string name)
    {
        if (n is JsonArray arr && arr.Count is 2 or 3)
        {
            try
            {
                double x = arr[0]!.GetValue<double>(), y = arr[1]!.GetValue<double>();
                double z = arr.Count == 3 ? arr[2]!.GetValue<double>() : 0;
                return new Point3d(x, y, z);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException) { }
        }
        throw Bad(name, "a point [x, y] or [x, y, z]");
    }

    /// <summary>A string or a list of strings; null when absent.</summary>
    public static List<string>? StrList(JsonObject a, string name)
    {
        var n = a[name];
        if (n == null) return null;
        if (n is JsonValue v && v.TryGetValue(out string? one)) return [one];
        if (n is JsonArray arr)
        {
            var list = new List<string>();
            foreach (var item in arr)
            {
                if (item is JsonValue iv && iv.TryGetValue(out string? s)) list.Add(s);
                else throw Bad(name, "a string or a list of strings");
            }
            return list;
        }
        throw Bad(name, "a string or a list of strings");
    }

    public static JsonObject? Obj(JsonObject a, string name) =>
        a[name] switch { null => null, JsonObject o => o, _ => throw Bad(name, "an object") };

    private static BridgeException Missing(string name) => new("bad_request", $"'{name}' is required");
    private static BridgeException Bad(string name, string what) => new("bad_request", $"'{name}' must be {what}");
}
