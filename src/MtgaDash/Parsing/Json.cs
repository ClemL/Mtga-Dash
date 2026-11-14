using System.Text.Json;

namespace MtgaDash.Parsing;

/// <summary>
/// Lenient JSON access. MTGA's casing is not consistent across endpoints (<c>WcTrackPosition</c> in
/// StartHook, <c>wcTrackPosition</c> elsewhere), so every lookup is case-insensitive.
/// </summary>
internal static class Json
{
    public static JsonElement? Get(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (obj.TryGetProperty(name, out var exact)) return exact;
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    /// <summary>Breadth-first search for a property, for objects MTGA nests under another key.</summary>
    public static JsonElement? Find(JsonElement obj, string name, int maxDepth)
    {
        var level = new List<JsonElement> { obj };
        for (var depth = 0; depth <= maxDepth && level.Count > 0; depth++)
        {
            var next = new List<JsonElement>();
            foreach (var el in level)
            {
                if (Get(el, name) is { } hit) return hit;
                if (el.ValueKind == JsonValueKind.Object)
                    next.AddRange(el.EnumerateObject().Select(p => p.Value).Where(v => v.ValueKind == JsonValueKind.Object));
            }
            level = next;
        }
        return null;
    }

    public static string? String(JsonElement obj, string name) => Get(obj, name) switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        _ => null,
    };

    public static int? Int(JsonElement obj, string name) => Get(obj, name) switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var i) => i,
        { ValueKind: JsonValueKind.Number } n when n.TryGetDouble(out var d) => (int)d,
        { ValueKind: JsonValueKind.String } s when int.TryParse(s.GetString(), out var i) => i,
        _ => null,
    };

    public static double? Double(JsonElement obj, string name) => Get(obj, name) switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
        { ValueKind: JsonValueKind.String } s when double.TryParse(s.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    /// <summary>Deck attribute values are JSON-quoted strings (<c>"\"Standard\""</c>); plain values pass through.</summary>
    public static string? UnquoteAttribute(string? value)
    {
        if (value is null || value.Length < 2 || value[0] != '"') return value;
        try
        {
            return JsonSerializer.Deserialize<string>(value);
        }
        catch (JsonException)
        {
            return value.Trim('"');
        }
    }

    /// <summary>
    /// Parses a JSON string value as JSON, repeatedly, until it is no longer a string. Requests arrive as
    /// strings of JSON, sometimes encoded twice.
    /// </summary>
    public static Unwrapped Unwrap(JsonElement element)
    {
        var docs = new List<JsonDocument>();
        for (var i = 0; i < 4 && element.ValueKind == JsonValueKind.String; i++)
        {
            var text = element.GetString();
            if (string.IsNullOrWhiteSpace(text)) break;
            var t = text.TrimStart();
            if (t[0] != '{' && t[0] != '[' && t[0] != '"') break;
            var doc = JsonDocument.Parse(text);
            docs.Add(doc);
            element = doc.RootElement;
        }
        return new Unwrapped(element, docs);
    }

    public readonly struct Unwrapped(JsonElement element, List<JsonDocument> docs) : IDisposable
    {
        public JsonElement Element { get; } = element;

        public void Dispose()
        {
            foreach (var d in docs) d.Dispose();
        }
    }
}
